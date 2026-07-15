using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed record NdJsonVisibleLine(int LineNumber, string Text);

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const double LineHeight = 20;
    private const int WindowViewportCount = 5;
    private const int WindowShiftViewportCount = 1;
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

    private IReadOnlyList<NdJsonVisibleLine> visibleLines = Array.Empty<NdJsonVisibleLine>();

    public IReadOnlyList<NdJsonVisibleLine> VisibleLines
    {
        get => visibleLines;
        private set => SetField(ref visibleLines, value);
    }

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
        EnsureWindow(0, InitialViewportLineCount);
    }

    public bool EnsureWindow(int firstVisibleIndex, int viewportLineCount)
    {
        //Console.WriteLine(
        //    $"EnsureWindow request: firstVisibleIndex={firstVisibleIndex}, viewportLineCount={viewportLineCount}, currentWindowStart={currentWindowStart}, currentWindowCount={currentWindowCount}, lineCount={(this.index?.LineCount ?? 0)}");

        if (this.index is null || this.mmap is null || this.index.LineCount == 0)
        {
            VisibleLines = Array.Empty<NdJsonVisibleLine>();
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            Console.WriteLine("EnsureWindow: no index or mmap available, cleared window.");
            return false;
        }

        viewportLineCount = Math.Max(1, viewportLineCount);
        lastFirstVisibleIndex = firstVisibleIndex;
        lastViewportLineCount = viewportLineCount;

        int lineCount = this.index.LineCount;
        int windowSize = Math.Min(viewportLineCount * WindowViewportCount, lineCount);
        int maxStartIndex = Math.Max(0, lineCount - windowSize);

        int startIndex;
        if (currentWindowStart < 0 || currentWindowCount < 0)
        {
            startIndex = Math.Max(0, firstVisibleIndex - viewportLineCount * WindowShiftViewportCount);
        }
        else
        {
            int lowerSafeBoundary = currentWindowStart + viewportLineCount * SafeViewportInset;
            int upperSafeBoundary = currentWindowStart + viewportLineCount * (WindowViewportCount - SafeViewportInset);
            startIndex = currentWindowStart;

            while (firstVisibleIndex < lowerSafeBoundary && startIndex > 0)
            {
                startIndex = Math.Max(0, startIndex - viewportLineCount * WindowShiftViewportCount);
                lowerSafeBoundary = startIndex + viewportLineCount * SafeViewportInset;
                upperSafeBoundary = startIndex + viewportLineCount * (WindowViewportCount - SafeViewportInset);
            }

            while (firstVisibleIndex > upperSafeBoundary && startIndex < maxStartIndex)
            {
                startIndex = Math.Min(maxStartIndex, startIndex + viewportLineCount * WindowShiftViewportCount);
                lowerSafeBoundary = startIndex + viewportLineCount * SafeViewportInset;
                upperSafeBoundary = startIndex + viewportLineCount * (WindowViewportCount - SafeViewportInset);
            }

            if (startIndex == currentWindowStart)
            {
                Console.WriteLine(
                    $"EnsureWindow: within hysteresis band, keeping current window start={currentWindowStart}, count={currentWindowCount}");
                return false;
            }
        }

        startIndex = Math.Clamp(startIndex, 0, maxStartIndex);

        int count = Math.Min(windowSize, lineCount - startIndex);

        if (currentWindowStart == startIndex && currentWindowCount == count)
        {
            Console.WriteLine(
                $"EnsureWindow: computed window matches current window start={currentWindowStart}, count={currentWindowCount}");
            return false;
        }

        Console.WriteLine(
            $"EnsureWindow: loading new window start={startIndex}, count={count}, previousStart={currentWindowStart}, previousCount={currentWindowCount}, topSpacer={startIndex * LineHeight}, bottomSpacer={(lineCount - startIndex - count) * LineHeight}");

        var newItems = new List<NdJsonVisibleLine>(count);
        for (int i = startIndex; i < startIndex + count; i++)
            newItems.Add(new NdJsonVisibleLine(i + 1, ReadLine(i)));

        VisibleLines = newItems;
        currentWindowStart = startIndex;
        currentWindowCount = count;
        TopSpacerHeight = startIndex * LineHeight;
        BottomSpacerHeight = (lineCount - startIndex - count) * LineHeight;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWindowStart)));
        return true;
    }

    public void RefreshWindow()
    {
        EnsureWindow(lastFirstVisibleIndex, lastViewportLineCount);
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
