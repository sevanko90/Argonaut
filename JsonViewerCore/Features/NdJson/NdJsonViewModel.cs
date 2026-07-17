using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using JsonViewerCore.Features.Json;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const int InitialIndexedLineTarget = 250;

    private MMapFile? mmap;
    private FileOffsetIndex? index;
    private MemoryMappedFileLineCollection? lines;
    private NdJsonSelectedLine? selectedLine;
    private JsonViewModel? selectedLineJsonViewModel;
    private long selectionRequestId;

    public string FilePath { get; private set; } = string.Empty;

    public int LineCount => this.index?.LineCount ?? 0;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public MemoryMappedFileLineCollection Lines => lines ?? throw new InvalidOperationException("LoadAsync must complete before Lines is accessed.");

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

    public JsonViewModel? SelectedLineJsonViewModel
    {
        get => selectedLineJsonViewModel;
        private set => SetField(ref selectedLineJsonViewModel, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NdJsonViewModel()
    {
    }

    public async Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        FilePath = path;

        var mmap = new MMapFile(path);
        var index = FileOffsetIndex.StartIndexing(mmap, progressReporter);
        this.mmap = mmap;
        this.index = index;
        IndexingTask = index.IndexingTask;

        // Await a small initial batch so the first paint isn't a totally empty scrollbar;
        // Lines.Count then tracks index.LineCount live as indexing continues in the background.
        await index.WaitForLineCountAsync(InitialIndexedLineTarget);

        SelectedLine = null;
        lines = new MemoryMappedFileLineCollection(index, mmap);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lines)));
    }

    public string GetLineText(int lineIndex)
    {
        return NdJsonLineReader.ReadLine(this.mmap!, this.index!.GetLineSpan(lineIndex));
    }

    public void LoadSelectedLine(int lineIndex)
    {
        var lineSpan = this.index!.GetLineSpan(lineIndex);
        SelectedLine = new NdJsonSelectedLine(lineIndex + 1, NdJsonLineReader.ReadLine(this.mmap!, lineSpan));

        var requestId = ++selectionRequestId;
        var previous = SelectedLineJsonViewModel;
        SelectedLineJsonViewModel = null;
        previous?.Dispose();

        _ = LoadSelectedLineJsonAsync(requestId, lineSpan);
    }

    private async Task LoadSelectedLineJsonAsync(long requestId, FileLineSpan lineSpan)
    {
        var trimmed = NdJsonLineReader.TrimTrailingNewline(this.mmap!, lineSpan);
        var jsonViewModel = new JsonViewModel();
        try
        {
            await jsonViewModel.LoadAsync(new MMapFile(FilePath, trimmed.Offset, trimmed.Length));
        }
        catch
        {
            jsonViewModel.Dispose();
            return;
        }

        if (requestId != selectionRequestId)
        {
            jsonViewModel.Dispose();
            return;
        }

        SelectedLineJsonViewModel = jsonViewModel;
    }

    public void Dispose()
    {
        lines?.Dispose();
        selectedLineJsonViewModel?.Dispose();
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
