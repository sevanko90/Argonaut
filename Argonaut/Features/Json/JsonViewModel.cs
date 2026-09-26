using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Search;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Find;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json;

/// <summary>
/// A JSON document shown as a tree. The tree reads the bytes through a sparse index
/// (<see cref="JsonSparseIndex"/>) and a <see cref="TreeDocument"/> the view's surface draws;
/// nothing holds a record per token, and the tree can be shown before indexing has got anywhere.
/// Selection, reveals and path segments are byte offsets - the start of the row they name.
/// </summary>
public sealed class JsonViewModel : IndexedDocumentViewModel, IPathNavigable, IByteRangeNavigable
{
    /// <summary>How often a still-indexing document tells its surface there is more to show.</summary>
    private static readonly TimeSpan GrowthInterval = TimeSpan.FromMilliseconds(500);

    private IndexedSourceSession<JsonSparseIndex>? session;
    private TreeDocument? tree;
    private JsonTreeReader? reader;
    private JsonTreeText? text;
    private JsonSchemaResolver? schemaResolver;
    private TreeExpandState? expand;
    private IndexGrowthMonitor? growthMonitor;
    private TreeRow? selectedRow;
    private string? selectedPath;
    private string? selectedValueText;
    private string? highlightTerm;
    private IReadOnlyList<JsonTreePathSegment> selectedPathSegments = Array.Empty<JsonTreePathSegment>();

    protected override IDocumentSession? Session => session;

    protected override IDisposable? MappedRows => growthMonitor is null && tree is null ? null : new CloseTree(this);

    internal IByteSource? Bytes => session?.Bytes;

    /// <summary>The sparse index, once loading has started.</summary>
    internal JsonSparseIndex? Index => session?.Index;

    /// <summary>
    /// What a find should scan for this document: the whole file for a top-level load, or just
    /// this line's byte range for the nested per-line view model NdJsonViewModel hosts. Carrying
    /// the range matters - the nested document's offsets are zero-based at the line start, so a
    /// whole-file scan would report offsets it cannot resolve (see ScanTarget).
    /// </summary>
    internal ScanTarget ScanTarget { get; private set; }

    /// <summary>Fires when this document begins tearing down, for
    /// <see cref="ISearchNavigator.DocumentTearingDown"/> - a find reveal links it so it stops
    /// rather than touching a released mapping.</summary>
    internal CancellationToken TearingDown => session?.TearingDown ?? default;

    /// <summary>The tree the view's surface draws. Null until LoadAsync has started.</summary>
    public TreeDocument? Tree => tree;

    /// <summary>Session state for date hints: the file-level default scheme (inferred or
    /// user-picked) and any per-value overrides. Created eagerly so MainWindow/NdJson can attach
    /// to it before or during load.</summary>
    public DateHintSettings HintSettings { get; } = new();

    /// <summary>Session state for schema hints: the schemas on offer, the selected one and its
    /// parsed document. Created eagerly, like <see cref="HintSettings"/>, so the toolbar and
    /// NdJsonViewModel can attach before or during load.</summary>
    public JsonSchemaSettings SchemaSettings { get; } = new();

    /// <summary>
    /// How many container levels are expanded by default. Set before LoadAsync to affect the
    /// initial view; <see cref="SetDefaultExpandDepth"/> changes it afterwards.
    /// </summary>
    public int DefaultExpandDepth { get; set; } = 2;

    private JsonToolbarViewModel? toolbar;

    /// <summary>This document's header toolbar (see <see cref="IDocumentViewModel.Toolbar"/>).
    /// Null until LoadAsync creates it; always null for the nested per-NDJSON-line instances,
    /// since those are never a shell document.</summary>
    public override JsonToolbarViewModel? Toolbar => toolbar;

    /// <summary>The selected row, or null.</summary>
    public TreeRow? SelectedRow
    {
        get => selectedRow;
        private set => SetField(ref selectedRow, value);
    }

    public string? SelectedPath
    {
        get => selectedPath;
        private set => SetField(ref selectedPath, value);
    }

    public IReadOnlyList<JsonTreePathSegment> SelectedPathSegments
    {
        get => selectedPathSegments;
        private set => SetField(ref selectedPathSegments, value);
    }

    /// <summary>The selected row's value as copy-value puts it on the clipboard: a string without
    /// its quotes, anything else as shown.</summary>
    public string? SelectedValueText
    {
        get => selectedValueText;
        private set => SetField(ref selectedValueText, value);
    }

    /// <summary>
    /// The active find term; rows highlight it in their displayed text. Null when no find is
    /// active.
    /// </summary>
    public string? HighlightTerm
    {
        get => highlightTerm;
        set => SetField(ref highlightTerm, value);
    }

    /// <summary>
    /// The offset a reveal is waiting to show - set by <see cref="Reveal"/> and consumed by the
    /// view, which may not exist yet when it is asked for (a search reveal into an NDJSON line
    /// selects the line first, and its view arrives a moment later).
    /// </summary>
    public long? PendingReveal { get; private set; }

    /// <summary>A reveal was asked for; the view shows <see cref="PendingReveal"/>.</summary>
    public event EventHandler? RevealRequested;

    /// <summary>What rows say changed - a schema bound, a hint setting - though the rows did not.</summary>
    public event EventHandler? RowsInvalidated;

    /// <summary>The default expand depth changed: the rows on screen are different rows.</summary>
    public event EventHandler? ExpansionReset;

    private readonly JsonViewSettings viewSettings;
    private readonly SchemaBindings schemaBindings;
    private readonly JsonSchemaCatalog schemaCatalog;

    /// <param name="viewSettings">Where the default expand depth is remembered.</param>
    /// <param name="schemaBindings">Where the schema chosen for each document is remembered.</param>
    /// <param name="schemaCatalog">The schemas a document can be bound to.</param>
    public JsonViewModel(JsonViewSettings viewSettings, SchemaBindings schemaBindings, JsonSchemaCatalog schemaCatalog)
    {
        this.viewSettings = viewSettings;
        this.schemaBindings = schemaBindings;
        this.schemaCatalog = schemaCatalog;

        SchemaSettings.SchemaChanged += OnSchemaChanged;
        SchemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;
        HintSettings.HintsChanged += OnHintsChanged;
    }

    private void OnHintsChanged(object? sender, EventArgs e) => RowsInvalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>The bound schema changed (a new selection finished loading, or was cleared) -
    /// rebind the rows. Null-safe for the window before loading has started; LoadCore applies
    /// whatever is current once it builds the tree.</summary>
    private void OnSchemaChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        if (schemaResolver is not null)
            schemaResolver.Schema = SchemaSettings.Document;
        RowsInvalidated?.Invoke(this, EventArgs.Empty);
        UpdateSchemaRootMatches();
    }

    /// <summary>
    /// Scores the bound schema's selectable types against the property names this document
    /// actually carries, so the type picker can lead with the likely answers instead of an
    /// alphabetical list of a hundred opaque names. Cheap enough to run inline - a bounded key
    /// sample, then a linear merge per candidate - and only reached for a schema offering a
    /// choice at all. Silent when the sample is empty; <see cref="OnIndexingCompleted"/> calls
    /// back.
    /// </summary>
    private void UpdateSchemaRootMatches()
    {
        if (SchemaSettings.Document is not { } schema || schema.NamedRoots.Count == 0 || reader is null || text is null)
            return;

        var keys = JsonDocumentKeySampler.ReadRootKeys(reader, text, out bool fromArrayElement);
        if (keys.Count == 0)
            return;

        SchemaSettings.SetRootMatches(JsonSchemaRootMatcher.Rank(schema, keys), fromArrayElement);
    }

    /// <summary>
    /// Persists the schema choice against this document so reopening the file restores it.
    /// Keyed on the *selection* rather than the loaded document, so a schema that fails to parse
    /// is still remembered as the user's choice. Skipped for the nested per-NDJSON-line
    /// instances, whose selection is driven from - and persisted by - the owning NdJsonViewModel.
    /// </summary>
    private void OnSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(JsonSchemaSettings.SelectedEntry) or nameof(JsonSchemaSettings.SelectedRootName)) || Toolbar is null)
            return;

        // Keyed by path, so a document without one (a paste) does not remember its choice -
        // keying it by display name would let two different pastes overwrite each other.
        if (Origin?.Path is { } documentPath)
            schemaBindings.Remember(documentPath, SchemaSettings.SelectedEntry?.FilePath, SchemaSettings.IsRootExplicitlyChosen ? SchemaSettings.SelectedRootName : null);
    }

    /// <summary>
    /// Asks the view to select and show the row holding <paramref name="offset"/>, expanding
    /// whatever hides it. An offset the index has not reached yet - a search hit racing ahead of
    /// a multi-GB scan - waits for it: seeking there sooner would read every sibling from the
    /// last indexed point to the target on the UI thread.
    /// </summary>
    public void Reveal(long offset)
    {
        PendingReveal = offset;
        if (IsCovered(offset))
            RevealRequested?.Invoke(this, EventArgs.Empty);
        else
            _ = RevealWhenCoveredAsync(offset);
    }

    private bool IsCovered(long offset)
        => session is not { } current || current.Index.AllItemsPublished
           || current.Index.Structure.IsComplete || offset < current.Index.Structure.ScannedTo;

    private async Task RevealWhenCoveredAsync(long offset)
    {
        var current = session!;
        try
        {
            while (!IsCovered(offset))
                await Task.Delay(100, current.TearingDown);
        }
        catch (OperationCanceledException)
        {
            return; // the document closed while waiting
        }

        // A later reveal replaced this one while it waited.
        if (!IsDisposed && PendingReveal == offset)
            RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The view has shown <see cref="PendingReveal"/>.</summary>
    public void ClearPendingReveal() => PendingReveal = null;

    /// <summary>
    /// The selected node's own bytes, relative to the input rather than to this document's
    /// source (an NDJSON line's tree is offset by its line start): a string with its quotes, a
    /// container bracket to bracket, a scalar as written. The property name is not part of it -
    /// the node is the value - and a closing row stands for the container it closes. A container
    /// whose end is not known cheaply is only a position (see <see cref="KnownEnd"/>).
    /// </summary>
    public ByteRange? SelectedByteRange
    {
        get
        {
            if (SelectedRow is not { } row || session is not { } current || reader is null)
                return null;

            var node = row.Node;
            long start = ScanTarget.Offset + node.ValueStart;
            return KnownEnd(current.Index.Structure, node) is { } end
                ? new ByteRange(start, end - node.ValueStart)
                : ByteRange.At(start);
        }
    }

    /// <summary>
    /// Where <paramref name="node"/> ends, when that costs a bounded read: a scalar's recorded end,
    /// a recorded container's end once it has closed, or - for a container the scan has gone far
    /// enough past to have recorded were it large - a scan of fewer than
    /// <see cref="SparseContainerIndex.PromotionBytes"/>. Null for a container still open, or one
    /// the scan has not reached, whose end could be gigabytes away.
    /// </summary>
    private long? KnownEnd(SparseContainerIndex structure, TreeNode node)
    {
        if (!node.IsContainer)
            return node.ValueEnd;

        int record = structure.FindContainerStartingAt(node.ValueStart);
        if (record >= 0)
            return structure.GetContainer(record).End is var end and >= 0 ? end : null;

        bool small = structure.IsComplete || node.ValueStart + structure.PromotionBytes <= structure.ScannedTo;
        return small ? reader!.SkipValue(node.ValueStart) : null;
    }

    /// <summary>
    /// Selects the row <paramref name="range"/> starts in, exactly as a search hit there would:
    /// bytes between nodes (a property name, punctuation) resolve to the value they lead up to,
    /// and a closing bracket to its container's closing row. A range outside this document's
    /// bytes (another line of an NDJSON file) reveals nothing.
    /// </summary>
    public Task RevealByteRangeAsync(ByteRange range)
    {
        long offset = range.Offset - ScanTarget.Offset;
        if (session is null || IsDisposed || offset < 0 || (ScanTarget.Length >= 0 && offset > ScanTarget.Length))
            return Task.CompletedTask;

        Reveal(offset);
        return Task.CompletedTask;
    }

    /// <summary>Asks the shell to show the selected node in the raw text view, through
    /// <see cref="RawJumpService"/> - the view model never learns the shell exists.</summary>
    public void ShowSelectionInText()
    {
        if (SelectedByteRange is { } range)
            RawJumpService.Request(range);
    }

    /// <summary>
    /// The view's selection moved to <paramref name="row"/>: works out its JSONPath and its
    /// value. Only the row's ancestry is read, so this costs the same anywhere in any document.
    /// </summary>
    public void OnRowSelected(TreeRow? row)
    {
        SelectedRow = row;
        if (row is not { } selected || session is null || text is null || reader is null)
        {
            SelectedPath = null;
            SelectedPathSegments = Array.Empty<JsonTreePathSegment>();
            SelectedValueText = null;
            return;
        }

        var cursor = new TreeCursor(session.Index.Structure, reader, expand!);
        cursor.SeekTo(selected.Start);
        var segments = JsonTreePaths.Segments(cursor, text);
        SelectedPathSegments = segments;
        SelectedPath = JsonTreePaths.Format(segments);
        SelectedValueText = ValueText(selected);
    }

    private string ValueText(in TreeRow row)
    {
        if (row.Shape != TreeRowShape.Leaf)
            return text!.Summary(row.Shape == TreeRowShape.Close ? row with { Shape = TreeRowShape.Open, IsExpanded = false } : row);

        string shown = text!.Scalar(row, out bool truncated, out _, out _);
        if (row.Node.FormatKind != (byte)JsonTokenKind.String || shown.Length < 2)
            return shown;

        // Strip the quotes so the clipboard holds the raw value rather than a JSON-literal
        // rendering of it; a truncated string has no closing quote to strip.
        return truncated ? shown[1..] : shown[1..^1];
    }

    /// <summary>
    /// Resolves a JSONPath string (see <see cref="JsonTreePaths"/>) and reveals the target, or
    /// surfaces a toast on parse/lookup failure. Wired into <see cref="JsonToolbarViewModel"/>'s
    /// "Go to path" action. Runs as a dependent read so closing the document mid-resolve joins it
    /// before the mapping is released.
    /// </summary>
    public async Task NavigateToPathAsync(string path)
    {
        if (session is null || IsDisposed || reader is null || text is null)
        {
            ToastService.Show("No file loaded yet.");
            return;
        }

        var current = session;
        var currentReader = reader;
        var currentText = text;
        JsonTreePathResult result;
        try
        {
            result = await current.StartDependentRead(_ =>
                Task.Run(() => JsonTreePaths.Resolve(current.Index.Structure, currentReader, currentText, path)));
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                ToastService.Show($"Navigation failed: {ex.Message}");
            return;
        }

        if (IsDisposed)
            return;

        if (result.Target is { } target)
            Reveal(target);
        else
            ToastService.Show(result.Error ?? "Path not found.");
    }

    /// <summary>
    /// Whether this document can offer "view as table" on its array rows. False for the
    /// sub-range documents NDJSON nests per line: their offsets are relative to the line, not
    /// the file, so the table would map the wrong bytes.
    /// </summary>
    public bool SupportsArrayTable => ScanTarget.Offset == 0;

    /// <summary>
    /// Resolves the array starting at <paramref name="arrayStart"/> to a byte range and raises an
    /// <see cref="ArrayTableService"/> request for it. Raising rather than acting keeps this view
    /// model unaware of the shell, the same way the truncated-value link reaches the raw viewer
    /// through <see cref="RawJumpService"/>.
    /// </summary>
    public void RequestArrayTable(long arrayStart)
    {
        if (session is null || !SupportsArrayTable || reader is null || text is null)
            return;

        var cursor = new TreeCursor(session.Index.Structure, reader, new TreeExpandState(int.MaxValue));
        if (!cursor.SeekTo(arrayStart) || cursor.Current.Node.ValueStart != arrayStart)
            return;

        long end = text.End(cursor.Current.Node);
        if (end == long.MaxValue)
        {
            ToastService.Show("That array hasn't finished loading yet.");
            return;
        }

        ArrayTableService.Request(new ArrayTableRequest(
            Origin!, arrayStart, end - arrayStart, JsonTreePaths.Format(JsonTreePaths.Segments(cursor, text))));
    }

    /// <summary>
    /// Changes the default-expand depth and applies it immediately if a file is already loaded,
    /// in addition to affecting future loads.
    /// </summary>
    public void SetDefaultExpandDepth(int depth)
    {
        DefaultExpandDepth = depth;
        if (expand is null)
            return;

        expand.DefaultDepth = depth;
        expand.Reset();
        ExpansionReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The toolbar's expand-depth choice: remembered for the next document, then applied
    /// to this one.</summary>
    private void ChooseExpandDepth(int depth)
    {
        viewSettings.ExpandDepth = depth;
        SetDefaultExpandDepth(depth);
    }

    public Task LoadAsync(IByteOrigin origin, IProgressReporter? progressReporter = null)
    {
        Origin = origin;
        FilePath = origin.Path ?? origin.DisplayName;
        ScanTarget = new ScanTarget(origin);
        DefaultExpandDepth = viewSettings.ExpandDepth;
        toolbar = new JsonToolbarViewModel(HintSettings, SchemaSettings, DefaultExpandDepth, ChooseExpandDepth, NavigateToPathAsync,
            refreshSchemaEntries: () => RefreshSchemaEntriesAsync(origin.Path), openSchemaFolder: schemaCatalog.OpenUserDirectory);

        var loadTask = LoadCore(origin.Open(), progressReporter);

        // Runs alongside indexing rather than blocking the open: whichever finishes first, the
        // other side picks the schema up (LoadCore applies whatever is current when it builds
        // the tree; OnSchemaChanged handles the reverse order).
        _ = ApplyInitialSchemaAsync(origin.Path);

        return loadTask;
    }

    /// <summary>
    /// Populates the schema catalog for this document and applies the initial binding, if any: a
    /// <c>&lt;file&gt;.schema.json</c> sidecar wins, otherwise the schema last bound to this path
    /// (see <see cref="SchemaBindings"/>). Nothing here is ever an error - a missing sidecar and
    /// an unreadable schema folder both just mean "no schema".
    /// </summary>
    private async Task ApplyInitialSchemaAsync(string? documentPath)
    {
        var bindings = schemaBindings.Entries;
        var (entries, preselected, rootName) = await Task.Run(() => schemaCatalog.GatherForDocument(documentPath, bindings));
        if (IsDisposed)
            return;

        SchemaSettings.SetEntries(entries);

        if (preselected is { } entry)
            await SchemaSettings.SelectAsync(entry, rootName);
    }

    /// <summary>Re-lists the schema catalog without touching the current selection - so a
    /// schema dropped into the user folder mid-session shows up next time the combo opens.</summary>
    private async Task RefreshSchemaEntriesAsync(string? documentPath)
    {
        var bindings = schemaBindings.Entries;
        var (entries, _, _) = await Task.Run(() => schemaCatalog.GatherForDocument(documentPath, bindings));
        if (!IsDisposed)
            SchemaSettings.SetEntries(entries);
    }

    /// <summary>
    /// Loads the sub-document occupying the byte range [offset, offset + length) of
    /// <paramref name="origin"/> - e.g. one line of a larger NDJSON document. Asks the origin
    /// for its own independent sub-range source (released with this view model), so a caller
    /// never allocates a source this view model is then responsible for freeing.
    /// </summary>
    public Task LoadAsync(IByteOrigin origin, long offset, long length, IProgressReporter? progressReporter = null)
    {
        Origin = origin;
        FilePath = origin.Path ?? origin.DisplayName;
        ScanTarget = new ScanTarget(origin, offset, length);
        return LoadCore(origin.OpenRange(offset, length), progressReporter);
    }

    private Task LoadCore(IByteSource bytes, IProgressReporter? progressReporter)
    {
        var session = IndexedSourceSession<JsonSparseIndex>.Start(bytes, JsonSparseIndex.StartIndexing, progressReporter);
        this.session = session;

        // The tree reads the bytes directly, so it is shown at once: the index only speeds up
        // jumps, and grows underneath it.
        reader = new JsonTreeReader(session.Bytes);
        text = new JsonTreeText(session.Bytes, session.Index.Structure, reader);
        schemaResolver = new JsonSchemaResolver(session.Index.Structure, reader, text) { Schema = SchemaSettings.Document };
        expand = new TreeExpandState(DefaultExpandDepth);
        var painter = new JsonTreePainter(text, new IValueHintProvider[] { new DateHintProvider(HintSettings) }, SupportsArrayTable);
        var gutters = new ITreeGutter[] { new JsonSchemaGutter(schemaResolver, text) };
        var sourceBytes = session.Bytes;
        tree = new TreeDocument(session.Index.Structure, reader, painter, expand, () => sourceBytes.AvailableLength, gutters);

        if (!session.Index.AllItemsPublished)
        {
            var index = session.Index;
            growthMonitor = new IndexGrowthMonitor(GrowthInterval, index.IndexingTask, () => index.AllItemsPublished,
                () => tree?.NotifyGrew());
        }

        UpdateSchemaRootMatches();

        // Inference dereferences the mapping, so the session must join it before unmapping.
        _ = InferDefaultDateSchemeAsync(session, reader, text);

        StatusText = $"{FilePath} — indexing…";
        MonitorIndexing();
        return Task.CompletedTask;
    }

    public override ISearchNavigator CreateSearchNavigator() => new JsonSearchNavigator(this);

    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Json;
    }

    /// <summary>Indexing finished: reports the size, then re-scores schema roots against the
    /// complete document - the sample taken at open may have seen only the start.</summary>
    protected override void OnIndexingCompleted()
    {
        StatusText = $"{FilePath} — {FormatByteLength(session?.Bytes.AvailableLength ?? 0)}";
        tree?.NotifyGrew();
        UpdateSchemaRootMatches();
    }

    /// <summary>Indexing stopped early (failure, or cancellation on <paramref name="failure"/> null).</summary>
    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        IndexFailure = failure;
        StatusText = failure is { } f
            ? $"{FilePath} — indexing stopped at line {f.Line?.ToString("N0") ?? "?"}, column {f.Column?.ToString("N0") ?? "?"} — {f.ItemsIndexed:N0} tokens shown"
            : $"{FilePath} — indexing failed";
    }

    /// <summary>
    /// Reads at most DateHintInference.MaxValuesToScan values in the background for the first
    /// classifiable date, and sets it as the file default if found. Never a full-file scan.
    /// No-ops if the user has already picked a scheme.
    /// </summary>
    private async Task InferDefaultDateSchemeAsync(IndexedSourceSession<JsonSparseIndex> current, JsonTreeReader currentReader, JsonTreeText currentText)
    {
        try
        {
            var scheme = await current.StartDependentRead(tearingDown => Task.Run(() =>
            {
                tearingDown.ThrowIfCancellationRequested();
                return DateHintInference.FindFirstScheme(current.Index.Structure, currentReader, currentText, DateHintInference.MaxValuesToScan);
            }));
            if (!IsDisposed && scheme is { } inferred)
                HintSettings.TrySetInferredDefault(inferred);
        }
        catch
        {
            // Indexing failures are surfaced elsewhere; teardown also cancels this reader.
        }
    }

    private static string FormatByteLength(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.#} GB",
    };

    /// <summary>
    /// What the base disposes before releasing the session: the growth monitor stops, and every
    /// surface drawing the tree lets go of it, since another row drawn after the release would
    /// read an unmapped file.
    /// </summary>
    private sealed class CloseTree(JsonViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            owner.growthMonitor?.Dispose();
            owner.growthMonitor = null;
            owner.tree?.Close();
        }
    }
}
