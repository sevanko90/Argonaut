using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Features.NdJson;

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : ObservableObject, IDisposable
{
    private const int InitialIndexedLineTarget = 250;

    private IndexedFileSession<FileOffsetIndex>? session;
    private MemoryMappedFileLineCollection? lines;
    private NdJsonSelectedLine? selectedLine;
    private JsonViewModel? selectedLineJsonViewModel;
    private string? highlightTerm;
    private long selectionRequestId;

    public string FilePath { get; private set; } = string.Empty;

    internal MMapFile? Mmap => this.session?.File;

    internal FileOffsetIndex? Index => this.session?.Index;

    public int LineCount => this.session?.Index.LineCount ?? 0;

    public Task IndexingTask => this.session?.IndexingTask ?? Task.CompletedTask;

    public MemoryMappedFileLineCollection Lines => lines ?? throw new InvalidOperationException("LoadAsync must complete before Lines is accessed.");

    public NdJsonSelectedLine? SelectedLine
    {
        get => selectedLine;
        private set
        {
            if (!SetField(ref selectedLine, value))
                return;

            OnPropertyChanged(nameof(SelectedLineNumber));
            OnPropertyChanged(nameof(SelectedLineText));
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

    /// <summary>Default-expand depth applied to each selected line's nested JsonViewModel.</summary>
    public int DefaultExpandDepth { get; set; } = 2;

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

    /// <summary>
    /// Changes the default-expand depth for future line selections, and applies it
    /// immediately to the currently selected line's tree if one is open.
    /// </summary>
    public void SetDefaultExpandDepth(int depth)
    {
        DefaultExpandDepth = depth;
        selectedLineJsonViewModel?.SetDefaultExpandDepth(depth);
    }

    public async Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        FilePath = path;

        var session = IndexedFileSession<FileOffsetIndex>.Start(new MMapFile(path), FileOffsetIndex.StartIndexing, progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't a totally empty scrollbar;
        // Lines.Count then tracks index.LineCount live as indexing continues in the background.
        await session.Index.WaitForLineCountAsync(InitialIndexedLineTarget);

        SelectedLine = null;
        lines = new MemoryMappedFileLineCollection(session.Index, session.File);
        OnPropertyChanged(nameof(Lines));
    }

    public string GetLineText(int lineIndex)
    {
        return NdJsonLineReader.ReadLine(this.Mmap!, this.Index!.GetLineSpan(lineIndex));
    }

    public void LoadSelectedLine(int lineIndex)
    {
        var lineSpan = this.Index!.GetLineSpan(lineIndex);
        SelectedLine = new NdJsonSelectedLine(lineIndex + 1, NdJsonLineReader.ReadLine(this.Mmap!, lineSpan));

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
        var trimmed = NdJsonLineReader.TrimTrailingNewline(this.Mmap!, lineSpan);
        var jsonViewModel = new JsonViewModel { DefaultExpandDepth = DefaultExpandDepth };
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
        // Cancel first so the background line-offset scan stops promptly; the collections
        // and the nested per-line view model must be disposed before session.Dispose joins
        // the scan and releases the mapping.
        this.session?.Cancel();

        lines?.Dispose();
        if (selectedLineJsonViewModel is not null)
            selectedLineJsonViewModel.HintSettings.PropertyChanged -= OnChildHintSettingsPropertyChanged;
        selectedLineJsonViewModel?.Dispose();
        this.session?.Dispose();
    }
}
