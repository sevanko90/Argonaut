using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const int InitialIndexedLineTarget = 250;

    private MMapFile? mmap;
    private NdJsonOffsetIndex? index;
    private NdJsonLineCollection? lines;
    private NdJsonSelectedLine? selectedLine;

    public string FilePath { get; private set; } = string.Empty;

    public int LineCount => this.index?.LineCount ?? 0;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public NdJsonLineCollection Lines => lines ?? throw new InvalidOperationException("LoadAsync must complete before Lines is accessed.");

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
        var index = NdJsonOffsetIndex.StartIndexing(mmap, progressReporter);
        this.mmap = mmap;
        this.index = index;
        IndexingTask = index.IndexingTask;

        // Await a small initial batch so the first paint isn't a totally empty scrollbar;
        // Lines.Count then tracks index.LineCount live as indexing continues in the background.
        await index.WaitForLineCountAsync(InitialIndexedLineTarget);

        SelectedLine = null;
        lines = new NdJsonLineCollection(index, mmap);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lines)));
    }

    public string GetLineText(int lineIndex)
    {
        return ReadLine(this.index!.GetLineSpan(lineIndex));
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
        lines?.Dispose();
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
