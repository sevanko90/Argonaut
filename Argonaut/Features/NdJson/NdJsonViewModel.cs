using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Features.NdJson;

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const int InitialIndexedLineTarget = 250;

    private MMapFile? mmap;
    private FileOffsetIndex? index;
    private MemoryMappedFileLineCollection? lines;
    private NdJsonSelectedLine? selectedLine;
    private JsonViewModel? selectedLineJsonViewModel;
    private string? highlightTerm;
    private long selectionRequestId;

    public string FilePath { get; private set; } = string.Empty;

    internal MMapFile? Mmap => mmap;

    internal FileOffsetIndex? Index => index;

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

    /// <summary>
    /// Master date-hint settings shared across every line's nested JsonViewModel: the header
    /// dropdown attaches to this. Only the default scheme is shared - per-token overrides live
    /// on each line's own (disposed-per-selection) JsonViewModel.HintSettings and are never
    /// copied here.
    /// </summary>
    public DateHintSettings HintSettings { get; } = new();

    /// <summary>
    /// The active find term, highlighted in the line list and propagated into every nested
    /// per-line JsonViewModel (current and future) so the right-hand tree highlights too.
    /// </summary>
    public string? HighlightTerm
    {
        get => highlightTerm;
        set
        {
            if (!SetField(ref highlightTerm, value))
                return;

            if (selectedLineJsonViewModel is not null)
                selectedLineJsonViewModel.HighlightTerm = value;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NdJsonViewModel()
    {
        HintSettings.PropertyChanged += OnMasterHintSettingsPropertyChanged;
    }

    /// <summary>Pushes a master default-scheme or time-zone-mode change down into the currently
    /// selected line's nested JsonViewModel.</summary>
    private void OnMasterHintSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (selectedLineJsonViewModel is null)
            return;

        if (e.PropertyName is null or nameof(DateHintSettings.FileDefaultScheme))
        {
            if (HintSettings.IsUserSelected)
                selectedLineJsonViewModel.HintSettings.SetUserDefault(HintSettings.FileDefaultScheme);
            else if (HintSettings.FileDefaultScheme != DateDecodingScheme.Off)
                selectedLineJsonViewModel.HintSettings.TrySetInferredDefault(HintSettings.FileDefaultScheme);
        }

        if (e.PropertyName is null or nameof(DateHintSettings.TimeZoneMode))
            selectedLineJsonViewModel.HintSettings.SetTimeZoneMode(HintSettings.TimeZoneMode);
    }

    /// <summary>
    /// Promotes the current line's own inference (or user pick) up to the master default,
    /// so the first classified value on the first opened line sets the whole-file default.
    /// TrySetInferredDefault's no-op rules (plus equal-value no-ops on both sides) prevent
    /// ping-pong with OnMasterHintSettingsPropertyChanged.
    /// </summary>
    private void OnChildHintSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(DateHintSettings.FileDefaultScheme)))
            return;

        if (selectedLineJsonViewModel is null || !ReferenceEquals(sender, selectedLineJsonViewModel.HintSettings))
            return;

        if (!selectedLineJsonViewModel.HintSettings.IsUserSelected)
            HintSettings.TrySetInferredDefault(selectedLineJsonViewModel.HintSettings.FileDefaultScheme);
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
        if (previous is not null)
            previous.HintSettings.PropertyChanged -= OnChildHintSettingsPropertyChanged;
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

        // Re-copy in case the term changed while this line's JSON was loading.
        jsonViewModel.HighlightTerm = HighlightTerm;

        // Seed this line's date-hint default and time-zone mode from the shared master, then
        // keep it linked so this line's own inference (if the master doesn't have one yet) can
        // promote the whole-file default too.
        if (HintSettings.IsUserSelected)
            jsonViewModel.HintSettings.SetUserDefault(HintSettings.FileDefaultScheme);
        else if (HintSettings.FileDefaultScheme != DateDecodingScheme.Off)
            jsonViewModel.HintSettings.TrySetInferredDefault(HintSettings.FileDefaultScheme);
        jsonViewModel.HintSettings.SetTimeZoneMode(HintSettings.TimeZoneMode);
        jsonViewModel.HintSettings.PropertyChanged += OnChildHintSettingsPropertyChanged;

        SelectedLineJsonViewModel = jsonViewModel;
    }

    public void Dispose()
    {
        lines?.Dispose();
        if (selectedLineJsonViewModel is not null)
            selectedLineJsonViewModel.HintSettings.PropertyChanged -= OnChildHintSettingsPropertyChanged;
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
