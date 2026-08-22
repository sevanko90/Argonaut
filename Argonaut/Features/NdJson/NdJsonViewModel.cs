using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Features.NdJson;

public sealed record NdJsonSelectedLine(int LineNumber, string Text);

public sealed class NdJsonViewModel : ObservableObject, IDocumentViewModel
{
    private const int InitialIndexedLineTarget = 250;

    private IndexedFileSession<FileOffsetIndex>? session;
    private MemoryMappedFileLineCollection? lines;
    private NdJsonSelectedLine? selectedLine;
    private JsonViewModel? selectedLineJsonViewModel;
    private string? highlightTerm;
    private string statusText = string.Empty;
    private IndexFailure? indexFailure;
    private long selectionRequestId;
    private bool disposed;

    public string FilePath { get; private set; } = string.Empty;

    internal MMapFile? Mmap => this.session?.File;

    internal FileOffsetIndex? Index => this.session?.Index;

    public int LineCount => this.session?.Index.LineCount ?? 0;

    public Task IndexingTask => this.session?.IndexingTask ?? Task.CompletedTask;

    public MemoryMappedFileLineCollection Lines => lines ?? throw new InvalidOperationException("LoadAsync must complete before Lines is accessed.");

    /// <summary>See <see cref="IDocumentViewModel.IndexFailure"/>.</summary>
    public IndexFailure? IndexFailure
    {
        get => indexFailure;
        private set => SetField(ref indexFailure, value);
    }

    /// <summary>Status-bar line for this document (see <see cref="IDocumentViewModel"/>):
    /// line count plus the selected line, refreshed on selection changes and when
    /// indexing finishes.</summary>
    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public NdJsonSelectedLine? SelectedLine
    {
        get => selectedLine;
        private set
        {
            if (!SetField(ref selectedLine, value))
                return;

            OnPropertyChanged(nameof(SelectedLineNumber));
            OnPropertyChanged(nameof(SelectedLineText));
            UpdateStatusText();
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
    /// Master schema settings for the whole NDJSON file - every line of an NDJSON file has the
    /// same shape, so one schema selection covers all of them. The parsed
    /// <see cref="JsonSchemaDocument"/> is pushed into each line's nested JsonViewModel by
    /// reference, so selecting a schema parses it once, not once per line viewed.
    /// </summary>
    public JsonSchemaSettings SchemaSettings { get; } = new();

    /// <summary>Default-expand depth applied to each selected line's nested JsonViewModel.</summary>
    public int DefaultExpandDepth { get; set; } = 2;

    /// <summary>This document's header toolbar (see <see cref="IDocumentViewModel.Toolbar"/>).
    /// Null until <see cref="LoadAsync"/> creates it.</summary>
    public JsonToolbarViewModel? Toolbar { get; private set; }

    object? IDocumentViewModel.Toolbar => Toolbar;

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
        SchemaSettings.SchemaChanged += OnMasterSchemaChanged;
        SchemaSettings.PropertyChanged += OnMasterSchemaSettingsPropertyChanged;
    }

    /// <summary>Shares the newly-parsed schema with the line currently open in the tree.</summary>
    private void OnMasterSchemaChanged(object? sender, EventArgs e)
        => selectedLineJsonViewModel?.SchemaSettings.SetDocument(SchemaSettings.Document);

    /// <summary>Persists the schema choice against the NDJSON file itself, so reopening it
    /// restores the binding for every line.</summary>
    private void OnMasterSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(JsonSchemaSettings.SelectedEntry) or nameof(JsonSchemaSettings.SelectedRootName))
            SchemaSelectionPreference.Save(FilePath, SchemaSettings.SelectedEntry?.FilePath, SchemaSettings.IsRootExplicitlyChosen ? SchemaSettings.SelectedRootName : null);
    }

    /// <summary>Lifts the open line's schema-type match scores into the shared toolbar - see the
    /// remark where this is subscribed.</summary>
    private void OnChildSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is JsonSchemaSettings child && e.PropertyName is null or nameof(JsonSchemaSettings.RootMatches))
            SchemaSettings.SetRootMatches(child.RootMatches, child.MatchesDescribeArrayElements);
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
        DefaultExpandDepth = ExpandDepthPreference.Load();
        Toolbar = new JsonToolbarViewModel(HintSettings, SchemaSettings, DefaultExpandDepth, SetDefaultExpandDepth,
            refreshSchemaEntries: () => RefreshSchemaEntriesAsync(path));

        // Alongside indexing, not blocking it - see JsonViewModel.ApplyInitialSchemaAsync.
        _ = ApplyInitialSchemaAsync(path);

        var session = IndexedFileSession<FileOffsetIndex>.Start(new MMapFile(path), FileOffsetIndex.StartIndexing, progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't a totally empty scrollbar;
        // Lines.Count then tracks index.LineCount live as indexing continues in the background.
        await session.Index.WaitForLineCountAsync(InitialIndexedLineTarget);

        if (session.Index.Failure is { } failure)
            IndexFailure = failure;

        SelectedLine = null;
        lines = new MemoryMappedFileLineCollection(session.Index, session.File);
        OnPropertyChanged(nameof(Lines));

        UpdateStatusText();
        _ = MonitorIndexingAsync(session);
    }

    /// <summary>Populates the schema catalog and applies any sidecar/remembered binding for the
    /// NDJSON file - see <see cref="JsonSchemaCatalog.GatherForDocument"/>.</summary>
    private async Task ApplyInitialSchemaAsync(string documentPath)
    {
        var (entries, preselected, rootName) = await Task.Run(() => JsonSchemaCatalog.GatherForDocument(documentPath));
        if (disposed)
            return;

        SchemaSettings.SetEntries(entries);

        if (preselected is { } entry)
            await SchemaSettings.SelectAsync(entry, rootName);
    }

    /// <summary>Re-lists the schema catalog without touching the current selection - see
    /// <see cref="JsonViewModel.RefreshSchemaEntriesAsync"/>.</summary>
    private async Task RefreshSchemaEntriesAsync(string documentPath)
    {
        var (entries, _, _) = await Task.Run(() => JsonSchemaCatalog.GatherForDocument(documentPath));
        if (!disposed)
            SchemaSettings.SetEntries(entries);
    }

    public ISearchNavigator CreateSearchNavigator() => new NdJsonSearchNavigator(this);

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    public bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return (fileType == FileTypeDetector.FileKind.Ndjson);
    }

    private void UpdateStatusText()
    {
        StatusText = SelectedLineNumber is { } line
            ? $"{FilePath} — {LineCount:N0} lines — Selected line: {line:N0}"
            : $"{FilePath} — {LineCount:N0} lines";
    }

    /// <summary>
    /// Refreshes <see cref="StatusText"/> when background indexing finishes (keeping the
    /// "Selected line" suffix if one is selected by then) or fails. Fire-and-forget from
    /// LoadAsync (UI thread); the await resumes there per the app's threading convention.
    /// The disposed check covers cancellation-by-dispose: a superseded or closed document
    /// must not repaint its status as a failure.
    /// </summary>
    private async Task MonitorIndexingAsync(IndexedFileSession<FileOffsetIndex> session)
    {
        try
        {
            await session.IndexingTask;
        }
        catch
        {
            if (!disposed)
            {
                IndexFailure = session.Index.Failure;
                StatusText = session.Index.Failure is { } failure
                    ? $"{FilePath} — indexing stopped — {failure.ItemsIndexed:N0} lines shown"
                    : $"{FilePath} — indexing failed";
            }
            return;
        }

        if (!disposed)
            UpdateStatusText();
    }

    public string GetLineText(int lineIndex)
    {
        return NdJsonLineReader.ReadLine(this.Mmap!, this.Index!.GetLineSpan(lineIndex));
    }

    public void LoadSelectedLine(int lineIndex)
    {
        var lineSpan = this.Index!.GetLineSpan(lineIndex);
        // Display text only - the JSON tree below is parsed from lineSpan itself, uncapped.
        SelectedLine = new NdJsonSelectedLine(lineIndex + 1, NdJsonLineReader.ReadDisplayLine(this.Mmap!, lineSpan));

        var requestId = ++selectionRequestId;
        var previous = SelectedLineJsonViewModel;
        SelectedLineJsonViewModel = null;
        if (previous is not null)
        {
            previous.HintSettings.PropertyChanged -= OnChildHintSettingsPropertyChanged;
            previous.SchemaSettings.PropertyChanged -= OnChildSchemaSettingsPropertyChanged;
        }

        previous?.Dispose();

        _ = LoadSelectedLineJsonAsync(requestId, lineSpan);
    }

    private async Task LoadSelectedLineJsonAsync(long requestId, FileLineSpan lineSpan)
    {
        var trimmed = NdJsonLineReader.TrimTrailingNewline(this.Mmap!, lineSpan);
        var jsonViewModel = new JsonViewModel { DefaultExpandDepth = DefaultExpandDepth };
        try
        {
            await jsonViewModel.LoadAsync(FilePath, trimmed.Offset, trimmed.Length);
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

        // The already-parsed schema by reference - never re-parsed per line.
        jsonViewModel.SchemaSettings.SetDocument(SchemaSettings.Document);

        // Type matching is evidence taken from a document, and an NDJSON file's documents are its
        // lines - the master has no keys of its own to sample. So the scores flow the other way
        // from the schema: up from whichever line is open, into the shared toolbar.
        jsonViewModel.SchemaSettings.PropertyChanged += OnChildSchemaSettingsPropertyChanged;

        SelectedLineJsonViewModel = jsonViewModel;
    }

    public void Dispose()
    {
        // Idempotent - see IDocumentViewModel's lifetime contract.
        if (disposed)
            return;
        disposed = true;

        // Cancel first so the background line-offset scan stops promptly; the collections
        // and the nested per-line view model must be disposed before session.Dispose joins
        // the scan and releases the mapping.
        this.session?.Cancel();

        lines?.Dispose();
        if (selectedLineJsonViewModel is not null)
        {
            selectedLineJsonViewModel.HintSettings.PropertyChanged -= OnChildHintSettingsPropertyChanged;
            selectedLineJsonViewModel.SchemaSettings.PropertyChanged -= OnChildSchemaSettingsPropertyChanged;
        }
        selectedLineJsonViewModel?.Dispose();
        this.session?.Dispose();
    }
}
