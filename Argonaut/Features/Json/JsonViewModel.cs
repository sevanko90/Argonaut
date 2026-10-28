using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Search;
using Argonaut.Engine.Text;
using Argonaut.Engine.Hints;
using Argonaut.Ui.Hints;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Preview;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Find;
using Argonaut.Ui.Launching;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json;

/// <summary>
/// A JSON document shown as a tree. The tree reads the bytes through a sparse index
/// (<see cref="JsonSparseIndex"/>) and a <see cref="TreeDocument"/> the view's surface draws;
/// nothing holds a record per token, and the tree can be shown before indexing has got anywhere.
/// Selection, reveals and path segments are byte offsets - the start of the row they name.
/// </summary>
public sealed class JsonViewModel : TreeDocumentViewModel
{
    private JsonTreeReader? reader;
    private JsonTreeText? text;
    private JsonSchemaResolver? schemaResolver;

    // What the whole-document index was built over, so a finished one can be kept on the origin
    // for the next JSON view of it (see KeptIndexes). Null for an NDJSON line's document, whose
    // structure covers only its line, and while the origin's bytes are still arriving.
    private IndexBasis? indexBasis;

    /// <summary>The sparse index, once loading has started.</summary>
    internal JsonSparseIndex? Index => (JsonSparseIndex?)TreeSession?.Index;

    /// <summary>Session state for date hints: the file-level default scheme (inferred or
    /// user-picked) and any per-value overrides. Created eagerly so MainWindow/NdJson can attach
    /// to it before or during load.</summary>
    public DateHintSettings HintSettings { get; } = new();

    /// <summary>Session state for schema hints: the schemas on offer, the selected one and its
    /// parsed document. Created eagerly, like <see cref="HintSettings"/>, so the toolbar and
    /// NdJsonViewModel can attach before or during load.</summary>
    public JsonSchemaSettings SchemaSettings { get; } = new();

    private JsonToolbarViewModel? toolbar;

    // What reads the values on rows for hints; the previews a hint opens use the same ones.
    private IReadOnlyList<IValueHintProvider>? hintProviders;

    /// <summary>This document's header toolbar (see <see cref="IDocumentViewModel.Toolbar"/>).
    /// Null until LoadAsync creates it; always null for the nested per-NDJSON-line instances,
    /// since those are never a shell document.</summary>
    public override JsonToolbarViewModel? Toolbar => toolbar;

    private readonly SchemaBindings schemaBindings;
    private readonly JsonSchemaCatalog schemaCatalog;

    /// <param name="viewSettings">Where the default expand depth is remembered.</param>
    /// <param name="schemaBindings">Where the schema chosen for each document is remembered.</param>
    /// <param name="schemaCatalog">The schemas a document can be bound to.</param>
    /// <param name="launcher">Opens the links value hints offer; null opens none.</param>
    public JsonViewModel(JsonViewSettings viewSettings, SchemaBindings schemaBindings, JsonSchemaCatalog schemaCatalog,
        ISystemLauncher? launcher = null) : base(viewSettings, launcher)
    {
        this.schemaBindings = schemaBindings;
        this.schemaCatalog = schemaCatalog;

        SchemaSettings.SchemaChanged += OnSchemaChanged;
        SchemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;
        HintSettings.HintsChanged += OnHintsChanged;
    }

    private void OnHintsChanged(object? sender, EventArgs e) => InvalidateRows();

    /// <summary>The bound schema changed (a new selection finished loading, or was cleared) -
    /// rebind the rows. Null-safe for the window before loading has started; LoadCore applies
    /// whatever is current once it builds the tree.</summary>
    private void OnSchemaChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        if (schemaResolver is not null)
            schemaResolver.Schema = SchemaSettings.Document;
        InvalidateRows();
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
    /// What a hint's value encodes, ready to show: decoded off the UI thread from the value's
    /// bytes, which are copied out first so nothing reads the document from another thread. Null
    /// when it does not decode after all, or the document closed meanwhile.
    /// </summary>
    public async Task<HintPreview?> PreviewHintAsync(ExpandHintLink link)
    {
        if (Bytes is not { } bytes)
            return null;

        long start = link.ValueStart + 1;
        long length = link.ValueEnd - 1 - start;
        if (HintPreviews.TooLarge(length) is { } tooLarge)
            return tooLarge;

        byte[] raw = bytes.RequireContiguous(start, (int)length).ToArray();
        var shown = await HintPreviews.DecodeAsync(link.Kind, raw, hintProviders, HintSettings.TimeZoneMode);
        if (IsDisposed)
        {
            shown?.Preview.Dispose();
            return null;
        }

        return shown;
    }

    protected override IReadOnlyList<TreePathSegment> PathSegmentsOf(TreeCursor cursor) => JsonTreePaths.Segments(cursor, text!);

    protected override string FormatPath(IReadOnlyList<TreePathSegment> segments) => JsonTreePaths.Format(segments);

    protected override TreePathResult ResolvePath(SparseContainerIndex structure, string path)
        => JsonTreePaths.Resolve(structure, reader!, text!, path);

    /// <summary>A string without its quotes, anything else as shown.</summary>
    protected override string ValueTextOf(in TreeRow row)
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
        if (TreeSession is not { } session || !SupportsArrayTable || reader is null || text is null)
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

    public Task LoadAsync(IByteOrigin origin, IProgressReporter? progressReporter = null)
    {
        AdoptOrigin(origin, new ScanTarget(origin));
        DefaultExpandDepth = ViewSettings.ExpandDepth;
        toolbar = new JsonToolbarViewModel(HintSettings, SchemaSettings, DefaultExpandDepth, ChooseExpandDepth, NavigateToPathAsync,
            refreshSchemaEntries: () => RefreshSchemaEntriesAsync(origin.Path), openSchemaFolder: schemaCatalog.OpenUserDirectory,
            showIndentGuides: ShowIndentGuides, applyIndentGuides: ChooseIndentGuides);

        indexBasis = IndexBasis.Of(origin);
        var loadTask = LoadCore(origin.Open(), progressReporter, indexBasis?.FindKept<JsonKeptStructure>(JsonKeptStructure.Key));

        // Runs alongside indexing rather than blocking the open: whichever finishes first, the
        // other side picks the schema up (LoadCore applies whatever is current when it builds
        // the tree; OnSchemaChanged handles the reverse order).
        _ = ApplyInitialSchemaAsync(origin.Path);

        return loadTask;
    }

    /// <summary>
    /// Populates the schema catalog for this document and applies the initial binding, if any: the
    /// schema last bound to this path (see <see cref="SchemaBindings"/>). Nothing here is ever an
    /// error - a missing schema and an unreadable schema folder both just mean "no schema".
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
        AdoptOrigin(origin, new ScanTarget(origin, offset, length));
        return LoadCore(origin.OpenRange(offset, length), progressReporter, kept: null);
    }

    /// <param name="kept">The structure a previous JSON view of the same bytes kept, if any - a
    /// save from the text view, or another program's edit, means there is none.</param>
    private Task LoadCore(IByteSource bytes, IProgressReporter? progressReporter, JsonKeptStructure? kept)
    {
        var session = StartSession(bytes,
            (source, progress, stopping) => JsonSparseIndex.StartIndexing(source, kept, progress, stopping), progressReporter);

        reader = new JsonTreeReader(session.Bytes);
        text = new JsonTreeText(session.Bytes, session.Index.Structure, reader);
        schemaResolver = new JsonSchemaResolver(session.Index.Structure, reader, text) { Schema = SchemaSettings.Document };
        hintProviders = ValueHintProviders.Standard(HintSettings, TimeProvider.System);
        var painter = new JsonTreePainter(text, hintProviders, SupportsArrayTable);
        var gutters = new ITreeGutter[] { new JsonSchemaGutter(schemaResolver, text) };
        ShowTree(reader, painter, gutters);

        UpdateSchemaRootMatches();

        // Inference dereferences the mapping, so the session must join it before unmapping.
        _ = InferDefaultDateSchemeAsync(session, reader, text);

        StatusText = DocumentStatusLine.Compose(FilePath, "indexing…");
        MonitorIndexing();
        return Task.CompletedTask;
    }

    public override ISearchNavigator CreateSearchNavigator() => new TreeSearchNavigator(this);

    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Json;
    }

    /// <summary>Indexing finished: reports the size, then re-scores schema roots against the
    /// complete document - the sample taken at open may have seen only the start.</summary>
    protected override void OnIndexingCompleted()
    {
        indexBasis?.Keep(JsonKeptStructure.Key, Index?.DetachStructure());
        StatusText = DocumentStatusLine.Compose(FilePath, ByteLengthText.Format(Bytes?.AvailableLength ?? 0));
        Tree?.NotifyGrew();
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
    private async Task InferDefaultDateSchemeAsync(IndexedSourceSession<ITreeIndex> current, JsonTreeReader currentReader, JsonTreeText currentText)
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
}
