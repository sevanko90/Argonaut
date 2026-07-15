using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed class NdJsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const double LineHeight = 20;
    private const int WindowMultiplier = 3;

    private readonly MMapFile mmap;
    private readonly NdJsonOffsetIndex index;
    private int currentWindowStart = -1;
    private int currentWindowCount = -1;
    private double topSpacerHeight;
    private double bottomSpacerHeight;
    private int? selectedLineNumber;

    private IReadOnlyList<string> visibleLines = Array.Empty<string>();

    public IReadOnlyList<string> VisibleLines
    {
        get => visibleLines;
        private set => SetField(ref visibleLines, value);
    }

    public string FilePath { get; }

    public int LineCount => this.index.LineCount;

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

    public int? SelectedLineNumber
    {
        get => selectedLineNumber;
        set => SetField(ref selectedLineNumber, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NdJsonViewModel(string path)
    {
        FilePath = path;
        this.mmap = new MMapFile(path);
        this.index = NdJsonOffsetIndex.Build(mmap);

        EnsureWindow(0, 50);
    }

    public void EnsureWindow(int firstVisibleIndex, int viewportLineCount)
    {
        if (this.index.LineCount == 0)
        {
            VisibleLines = Array.Empty<string>();
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            return;
        }

        viewportLineCount = Math.Max(1, viewportLineCount);

        int pageIndex = firstVisibleIndex / viewportLineCount;
        int startIndex = Math.Max(0, (pageIndex - 1) * viewportLineCount);
        int remaining = this.index.LineCount - startIndex;
        int count = Math.Min(viewportLineCount * WindowMultiplier, remaining);

        if (currentWindowStart == startIndex && currentWindowCount == count)
            return;

        var newItems = new List<string>(count);
        for (int i = startIndex; i < startIndex + count; i++)
            newItems.Add(ReadLine(i));

        VisibleLines = newItems;
        currentWindowStart = startIndex;
        currentWindowCount = count;
        TopSpacerHeight = startIndex * LineHeight;
        BottomSpacerHeight = (this.index.LineCount - startIndex - count) * LineHeight;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWindowStart)));
    }
    
    private string ReadLine(int lineIndex)
    {
        long offset = this.index.GetOffset(lineIndex);
        long length = this.index.GetLength(lineIndex, this.mmap.Length);

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            int bytesRead = this.mmap.Read(offset, buffer);

            return Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\r', '\n');
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        this.mmap.Dispose();
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
