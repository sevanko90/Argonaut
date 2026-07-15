using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

// Mutable so a window swap can update an existing row's content in place, reusing the
// ListBox container instead of tearing it down and rebuilding it (which caused a visible
// blip on every window load).
public sealed class NdJsonVisibleLine : INotifyPropertyChanged
{
    private int lineNumber;
    private string text;

    public NdJsonVisibleLine(int lineNumber, string text)
    {
        this.lineNumber = lineNumber;
        this.text = text;
    }

    public int LineNumber
    {
        get => lineNumber;
        set
        {
            if (lineNumber == value)
                return;
            lineNumber = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineNumber)));
        }
    }

    public string Text
    {
        get => text;
        set
        {
            if (text == value)
                return;
            text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const double LineHeight = 20;
    private const int WindowViewportCount = 5;
    private const int SafeViewportInset = 2;
    private const int InitialViewportLineCount = 50;
    private const int InitialIndexedLineTarget = InitialViewportLineCount * WindowViewportCount;

    private MMapFile? mmap;
    private NdJsonOffsetIndex? index;
    private int currentWindowStart = -1;
    private int currentWindowCount = -1;
    private int lastFirstVisibleIndex;
    private int lastViewportLineCount = InitialViewportLineCount;
    private double topSpacerHeight;
    private double bottomSpacerHeight;
    private NdJsonSelectedLine? selectedLine;
    private bool isLoading;

    // Materialization runs on a background thread; loadGeneration lets a newer request
    // supersede an in-flight one so only the latest window is ever applied. loadingStart/
    // loadingCount identify the window currently being read, to avoid restarting it.
    private int loadGeneration;
    private int loadingStart = -1;
    private int loadingCount = -1;
    private bool disposed;

    // Stable collection instance: ItemsSource binds to it once and we mutate it, so
    // container identity is preserved across window swaps. A window carries only the raw
    // line texts (StartIndex gives the line numbers) — no throwaway item objects.
    private readonly ObservableCollection<NdJsonVisibleLine> visibleLines = new();

    private sealed record MaterializedWindow(int StartIndex, int Count, string[] Texts);

    /// <summary>Raised on the UI thread whenever the displayed window changes, so the view can re-sync selection.</summary>
    public event Action? WindowApplied;

    public ObservableCollection<NdJsonVisibleLine> VisibleLines => visibleLines;

    public string FilePath { get; private set; } = string.Empty;

    public int LineCount => this.index?.LineCount ?? 0;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public double TopSpacerHeight
    {
        get => topSpacerHeight;
        private set => SetField(ref topSpacerHeight, value);
    }

    public double BottomSpacerHeight
    {
        get => bottomSpacerHeight;
        private set => SetField(ref bottomSpacerHeight, value);
    }

    public int CurrentWindowStart => currentWindowStart;

    /// <summary>True while a window is being read in the background and no content is displayed yet (fast scroll / drag).</summary>
    public bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }

    public NdJsonSelectedLine? SelectedLine
    {
        get => selectedLine;
        private set
        {
            if (!SetField(ref selectedLine, value))
                return;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLineNumber)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLineText)));
        }
    }

    public int? SelectedLineNumber => SelectedLine?.LineNumber;

    public string? SelectedLineText => SelectedLine?.Text;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NdJsonViewModel()
    {
    }

    public async Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        FilePath = path;

        var mmap = new MMapFile(path);
        var index = NdJsonOffsetIndex.Start(mmap, progressReporter);
        this.mmap = mmap;
        this.index = index;
        IndexingTask = index.IndexingTask;

        await index.WaitForLineCountAsync(InitialIndexedLineTarget);

        currentWindowStart = -1;
        currentWindowCount = -1;
        lastFirstVisibleIndex = 0;
        lastViewportLineCount = InitialViewportLineCount;
        SelectedLine = null;

        // First window: read synchronously so the file paints immediately (we already
        // awaited enough indexed lines above). Subsequent windows load in the background.
        int initialCount = Math.Min(InitialViewportLineCount * WindowViewportCount, index.LineCount);
        ApplyWindow(MaterializeWindow(0, initialCount));
    }

    public void EnsureWindow(int firstVisibleIndex, int viewportLineCount)
    {
        if (this.index is null || this.mmap is null || this.index.LineCount == 0)
        {
            if (visibleLines.Count > 0)
                visibleLines.Clear();
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            return;
        }

        viewportLineCount = Math.Max(1, viewportLineCount);
        lastFirstVisibleIndex = firstVisibleIndex;
        lastViewportLineCount = viewportLineCount;

        int lineCount = this.index.LineCount;
        int windowSize = Math.Min(viewportLineCount * WindowViewportCount, lineCount);
        int maxStartIndex = Math.Max(0, lineCount - windowSize);

        int startIndex;
        bool haveCurrent = currentWindowStart >= 0 && currentWindowCount >= 0;
        if (!haveCurrent)
        {
            startIndex = firstVisibleIndex - viewportLineCount * SafeViewportInset;
        }
        else
        {
            // Hysteresis band: while the viewport stays comfortably inside the loaded
            // window (SafeViewportInset viewports of buffer above and below), keep it.
            // The band edges sit ~1 viewport from the window edge, so crossing one is
            // our "running low on buffer" signal to prefetch the next window.
            int lowerSafeBoundary = currentWindowStart + viewportLineCount * SafeViewportInset;
            int upperSafeBoundary = currentWindowStart + viewportLineCount * (WindowViewportCount - SafeViewportInset);

            bool withinBand = firstVisibleIndex >= lowerSafeBoundary && firstVisibleIndex <= upperSafeBoundary;
            bool atTop = currentWindowStart == 0 && firstVisibleIndex < lowerSafeBoundary;
            bool atBottom = currentWindowStart == maxStartIndex && firstVisibleIndex > upperSafeBoundary;
            if (withinBand || atTop || atBottom)
                return;

            // Outside the band (including large scrollbar-drag jumps): recenter the
            // window on the viewport directly, in one pass.
            startIndex = firstVisibleIndex - viewportLineCount * SafeViewportInset;
        }

        startIndex = Math.Clamp(startIndex, 0, maxStartIndex);
        int count = Math.Min(windowSize, lineCount - startIndex);

        if (startIndex == currentWindowStart && count == currentWindowCount)
            return;

        // If the viewport is still inside the currently displayed window, this is pure
        // look-ahead: read the new window in the background and keep showing the current
        // one (no stutter, no blank). If not (fast jump), blank + spinner until it lands.
        bool viewportCovered = haveCurrent
            && firstVisibleIndex >= currentWindowStart
            && firstVisibleIndex + viewportLineCount <= currentWindowStart + currentWindowCount;

        StartLoad(startIndex, count, showSpinner: !viewportCovered);
    }

    public void RefreshWindow()
    {
        EnsureWindow(lastFirstVisibleIndex, lastViewportLineCount);
    }

    private void StartLoad(int startIndex, int count, bool showSpinner)
    {
        // Already reading exactly this window — let it finish.
        if (startIndex == loadingStart && count == loadingCount)
            return;

        int generation = ++loadGeneration;
        loadingStart = startIndex;
        loadingCount = count;

        if (showSpinner)
        {
            // Viewport moved off the loaded window: blank the content and position the
            // (empty) region at the target so the scrollbar/extent stay correct, then
            // show a spinner until the background read lands.
            currentWindowStart = -1;
            currentWindowCount = -1;
            if (visibleLines.Count > 0)
                visibleLines.Clear();
            int lineCount = this.index!.LineCount;
            TopSpacerHeight = startIndex * LineHeight;
            BottomSpacerHeight = Math.Max(0, (lineCount - startIndex) * LineHeight);
            IsLoading = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWindowStart)));
            WindowApplied?.Invoke();
        }

        Task.Run(() =>
        {
            try
            {
                var window = MaterializeWindow(startIndex, count);
                Dispatcher.UIThread.Post(() =>
                {
                    if (disposed || generation != loadGeneration)
                        return;
                    ApplyWindow(window);
                });
            }
            catch
            {
                // File disposed mid-read or a transient read failure; a newer load,
                // or the user nudging the scroll, will supersede this attempt.
            }
        });
    }

    private MaterializedWindow MaterializeWindow(int startIndex, int count)
    {
        var texts = new string[count];
        for (int i = 0; i < count; i++)
            texts[i] = ReadLine(startIndex + i);
        return new MaterializedWindow(startIndex, count, texts);
    }

    private void ApplyWindow(MaterializedWindow window)
    {
        int lineCount = this.index?.LineCount ?? (window.StartIndex + window.Count);

        // Update rows in place so the ListBox reuses its containers (no teardown/rebuild).
        // Membership only changes when the window size does (initial load and file ends).
        for (int i = 0; i < window.Count; i++)
        {
            int lineNumber = window.StartIndex + i + 1;
            string text = window.Texts[i];
            if (i < visibleLines.Count)
            {
                var line = visibleLines[i];
                line.LineNumber = lineNumber;
                line.Text = text;
            }
            else
            {
                visibleLines.Add(new NdJsonVisibleLine(lineNumber, text));
            }
        }

        for (int i = visibleLines.Count - 1; i >= window.Count; i--)
            visibleLines.RemoveAt(i);

        currentWindowStart = window.StartIndex;
        currentWindowCount = window.Count;
        loadingStart = -1;
        loadingCount = -1;
        TopSpacerHeight = window.StartIndex * LineHeight;
        BottomSpacerHeight = Math.Max(0, (lineCount - window.StartIndex - window.Count) * LineHeight);
        IsLoading = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWindowStart)));
        WindowApplied?.Invoke();
    }

    private string ReadLine(int lineIndex)
    {
        var lineSpan = this.index!.GetLineSpan(lineIndex);
        return ReadLine(lineSpan);
    }

    public string GetLineText(int lineIndex)
    {
        return ReadLine(lineIndex);
    }

    public void LoadSelectedLine(int lineIndex)
    {
        var lineSpan = this.index!.GetLineSpan(lineIndex);
        SelectedLine = new NdJsonSelectedLine(lineIndex + 1, ReadLine(lineSpan));
    }

    private string ReadLine(NdJsonLineSpan lineSpan)
    {
        var mmap = this.mmap!;
        long offset = lineSpan.Offset;
        long length = lineSpan.Length;

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            int bytesRead = mmap.Read(offset, buffer, (int)length);

            return Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\r', '\n');
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        disposed = true;
        loadGeneration++;
        this.mmap?.Dispose();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
