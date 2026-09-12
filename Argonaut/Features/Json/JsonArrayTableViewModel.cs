using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Argonaut.Features.Csv;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;
using Avalonia.Threading;

namespace Argonaut.Features.Json;

/// <summary>
/// One JSON array rendered as a CSV-style grid. A real <see cref="IDocumentViewModel"/>, entered
/// explicitly from the JSON tree and published directly by the shell (never via
/// <see cref="DocumentViewCatalog"/>) - exactly as a diff is, and for the same reason: it claims
/// no <see cref="FileTypeDetector.FileKind"/>, so the view switcher offers no selection for it
/// and picking any view there re-indexes the origin file through the normal switch path.
///
/// It owns an independent <see cref="JsonArrayTableSession"/> over the array's own byte range and
/// shares nothing with the JSON document it came from - which is required rather than tidy: the
/// shell disposes the outgoing document before publishing this one, so the origin's index and
/// mapping are already gone by the time this renders its first row.
///
/// Everything about status, failure reporting, the completion monitor and the disposal ordering
/// is <see cref="IndexedDocumentViewModel"/>'s; what is left here is the load, the column
/// discovery, and the toolbar.
/// </summary>
public sealed class JsonArrayTableViewModel : IndexedDocumentViewModel
{
    /// <summary>
    /// Elements awaited before the first paint, and the width sample. A ceiling, not a promise:
    /// the element index publishes in strides, so the sample is whatever has been published by
    /// then. Widths are a saturating heuristic, so 192 sampled elements and 250 give the same
    /// answer on any real data.
    /// </summary>
    private const int InitialElementTarget = 250;

    private JsonArrayTableSession? session;
    private JsonRowFactory? cellText;
    private JsonArrayRowCollection? rows;
    private JsonArrayTableToolbarViewModel? toolbar;
    private CsvStructure? structure;
    private ExpandedRoutes routes = ExpandedRoutes.None;
    private IReadOnlyList<ColumnNesting> nesting = [];
    private IReadOnlyList<JsonArrayColumnHeader> headers = [];
    private JsonArrayColumnMode mode = JsonArrayColumnMode.ByProperty;

    /// <summary>Containers the user has opened from a column header. Survives a re-shape, so
    /// going out to a reshape mode and back restores what was open.</summary>
    private readonly OpenColumns openColumns = new();

    private int arrayColumns = JsonArrayColumnDiscovery.DefaultArrayColumns;
    private JsonArrayCellDetail? cellDetail;

    protected override IDocumentSession? Session => this.session;

    protected override IDisposable? MappedRows => this.rows;

    public JsonArrayRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    public CsvStructure Structure => this.structure ?? throw new InvalidOperationException("LoadAsync must complete before Structure is accessed.");

    /// <summary>Columns discovered so far - 0 until <see cref="LoadAsync"/> has published a
    /// <see cref="Structure"/>, which is what the view waits for before building columns.</summary>
    public int ColumnCount => this.structure?.ColumnCount ?? 0;

    public int RowCount => this.rows?.Count ?? 0;

    /// <summary>Where each column's value sits inside an element. Flat - one property step per
    /// column - until a header expands something.</summary>
    public ExpandedRoutes Routes => this.routes;

    /// <summary>
    /// What the sample saw inside each column, parallel to <see cref="Structure"/>'s columns:
    /// which of them have anything nested to expand, and how wide expanding an array would be.
    /// Empty in a reshape mode, where a cell is a whole element and there is nothing to expand
    /// into.
    /// </summary>
    public IReadOnlyList<ColumnNesting> Nesting => this.nesting;

    /// <summary>
    /// One header per column of <see cref="Structure"/>, spelled out as the clickable pieces of
    /// its route. Replaced wholesale on every shape change, which is also what tells the view its
    /// columns are genuinely different ones rather than the same columns relabelled.
    /// </summary>
    public IReadOnlyList<JsonArrayColumnHeader> Headers => this.headers;

    /// <summary>
    /// The cell being shown in full beside the grid, or null when the pane is closed. Replaced by
    /// each new cell shown and disposed with the document - it holds a tree over the session's
    /// index, so it must not outlive it.
    /// </summary>
    public JsonArrayCellDetail? CellDetail
    {
        get => this.cellDetail;
        private set
        {
            var outgoing = this.cellDetail;
            if (ReferenceEquals(outgoing, value))
                return;

            this.cellDetail = value;
            outgoing?.Dispose();

            OnPropertyChanged(nameof(CellDetail));
            OnPropertyChanged(nameof(HasCellDetail));
        }
    }

    public bool HasCellDetail => this.cellDetail is not null;

    /// <summary>
    /// Opens the pane on one cell. An empty cell - an element missing that property, or a
    /// position past the end of a short row - has no value to show, so the pane is left as it
    /// was rather than blanked: clicking past the data should not throw away what the reader was
    /// looking at.
    /// </summary>
    public void ShowCell(int row, int column)
    {
        if (this.session is not { } current || this.rows is null || this.structure is null)
            return;

        if (column >= this.structure.ColumnCount)
            return;

        int token = this.rows.TokenForCell(row, column);
        if (token < 0)
            return;

        string path = column < this.headers.Count ? this.headers[column].Display : this.structure.Columns[column].Name;
        CellDetail = JsonArrayCellDetail.ForToken(current.Inner.Index, current.Inner.Bytes, token,
            $"{path} — row {row + 1:N0}");
    }

    public void CloseCellDetail() => CellDetail = null;

    /// <summary>Whether any column holds an array, and so whether how many positions an
    /// expansion draws is a question worth putting in the toolbar.</summary>
    public bool HasArrayColumns
    {
        get
        {
            foreach (var column in this.nesting)
            {
                if (column.HasArrays)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Positions an open array column draws. Changing it re-discovers the columns - which is a
    /// walk over the sample, not over the array - and leaves what is open alone.
    /// </summary>
    public int ArrayColumns
    {
        get => this.arrayColumns;
        set
        {
            int clamped = Math.Clamp(value, 1, JsonArrayColumnDiscovery.MaxArrayColumns);
            if (clamped == this.arrayColumns)
                return;

            this.arrayColumns = clamped;
            ReShape();
        }
    }

    /// <summary>
    /// Opens a container column, or closes it and everything under it - the click on a piece of a
    /// column header. Unknown keys are ignored rather than throwing: a header the user clicked is
    /// only as fresh as the last discovery.
    ///
    /// Re-discovers from the sample and re-shapes the grid. The array itself is never re-walked;
    /// element addressing does not depend on how the columns are drawn.
    /// </summary>
    public void ToggleColumn(string key)
    {
        if (this.session is null || this.rows is null || this.mode != JsonArrayColumnMode.ByProperty)
            return;

        this.openColumns.Toggle(key);
        ReShape();
    }

    public override object? Toolbar => this.toolbar;

    /// <summary>
    /// Null for v1: find is disabled while a table is showing. A navigator over token offsets in
    /// a sub-range mapping is separate work, and null is the contract's supported answer - the
    /// shell hides the find bar via IsFindAvailable. It also sidesteps
    /// <see cref="ISearchNavigator.DocumentTearingDown"/>, which deliberately has no default.
    /// </summary>
    public override ISearchNavigator? CreateSearchNavigator() => null;

    /// <summary>Never reached through the view catalog - this document is only ever constructed
    /// directly, like <see cref="Diff.JsonDiffViewModel"/>.</summary>
    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

    /// <summary>
    /// Opens the byte range [<paramref name="arrayOffset"/>, + <paramref name="arrayLength"/>) of
    /// <paramref name="filePath"/> - which must be exactly one array's <c>[</c>…<c>]</c> - as its
    /// own document, and renders its elements as a table. <paramref name="originPath"/> is the
    /// JSONPath the array sits at in the origin document, carried for the banner and for Back.
    ///
    /// Returns once the row collection exists; indexing and the element walk continue in the
    /// background, monitored for status and failure by the base class.
    /// </summary>
    public async Task LoadAsync(string filePath, long arrayOffset, long arrayLength, string originPath, Func<string, Task>? navigateBack = null)
    {
        FilePath = filePath;

        // Progress is reported by this document rather than through the shell's own reporter,
        // the way a diff does it - the entry point publishes directly and only silences the
        // outgoing load's reporter.
        var session = JsonArrayTableSession.Start(filePath, arrayOffset, arrayLength,
            new ProgressToStatus(this, $"Indexing {Path.GetFileName(filePath)}"));
        this.session = session;

        // A small initial batch so the first paint isn't an empty grid, and so there is a real
        // sample to width the columns from; a short array completes the wait via MarkComplete.
        await session.Elements.WaitForElementCountAsync(InitialElementTarget);
        if (IsDisposed)
            return;

        if (session.Failure is { } failure)
            IndexFailure = failure;

        // The same builder the row collection renders cells with, so column widths are measured
        // from the text that will actually be shown.
        this.cellText = new JsonRowFactory(session.Inner.Index, session.Inner.Bytes, hintProviders: null);

        var discovered = Discover(session);
        Adopt(discovered);
        bool elementsAreObjects = discovered.SawObject;

        this.rows = new JsonArrayRowCollection(session.Elements, session.Inner.Index, session.Inner.Bytes,
            discovered.Structure, this.routes, this.mode);

        // Built here rather than before the wait because it takes the answer discovery just
        // produced: an array of objects is already columned by its property names, so it is
        // offered no reshape widths and shows no picker.
        this.toolbar = new JsonArrayTableToolbarViewModel(originPath,
            canReshape: !elementsAreObjects,
            setColumnMode: ApplyColumnMode,
            setArrayColumns: columns => ArrayColumns = columns,
            back: () => navigateBack?.Invoke(originPath) ?? Task.CompletedTask);

        this.toolbar.ShowArrayColumns(HasArrayColumns);

        OnPropertyChanged(nameof(Toolbar));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Headers));
        OnPropertyChanged(nameof(Structure));
        OnPropertyChanged(nameof(ColumnCount));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{FilePath} — {RowCount:N0} rows indexed so far";
        MonitorIndexing();
    }

    /// <summary>The cell pane's tree reads the session's index, so it goes before the session
    /// does - which is exactly what DisposeCore runs between.</summary>
    protected override void DisposeCore()
    {
        this.cellDetail?.Dispose();
        this.cellDetail = null;
    }

    protected override void OnIndexingCompleted()
    {
        OnPropertyChanged(nameof(RowCount));
        StatusText = $"{FilePath} — {RowCount:N0} rows";
    }

    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        IndexFailure = failure;
        StatusText = failure is { } f
            ? $"{FilePath} — indexing stopped — {f.ItemsIndexed:N0} items indexed"
            : $"{FilePath} — indexing failed";
    }

    /// <summary>
    /// Re-shapes the grid for a new picker selection. Builds a new <see cref="CsvStructure"/> and
    /// hands it to the row collection; the array itself is never re-walked, because element
    /// addressing is independent of how the columns are drawn.
    /// </summary>
    private void ApplyColumnMode(JsonArrayColumnModeOption option)
    {
        if (this.session is not { } current || this.rows is null)
            return;

        this.mode = option.Mode;
        if (option.Mode == JsonArrayColumnMode.ByProperty)
        {
            Adopt(Discover(current));
        }
        else
        {
            // A reshape cell is a whole element, so there is no route into one, nothing to
            // report about what is nested in it, and no header piece to click.
            this.structure = BuildReshapeStructure(current, option.Columns);
            this.routes = ExpandedRoutes.None;
            this.nesting = [];
            this.headers = PlainHeaders(this.structure);
        }

        PublishShape();
    }

    /// <summary>Re-discovers the columns for the expansion state as it now is, and re-shapes the
    /// grid. Only ever called in by-property mode - the reshape modes have nothing to expand.</summary>
    private void ReShape()
    {
        if (this.session is not { } current || this.rows is null)
            return;

        Adopt(Discover(current));
        PublishShape();
    }

    private DiscoveredColumns Discover(JsonArrayTableSession current)
        => JsonArrayColumnDiscovery.FromSample(current.Inner.Index, current.Inner.Bytes, current.Elements,
            Math.Min(current.Elements.ElementCount, InitialElementTarget),
            this.cellText ?? throw new InvalidOperationException("The cell-text builder must exist before discovery."),
            this.openColumns, this.arrayColumns);

    private void Adopt(DiscoveredColumns discovered)
    {
        this.structure = discovered.Structure;
        this.routes = discovered.Routes;
        this.nesting = discovered.Nesting;
        this.headers = discovered.Headers;

        if (discovered.Truncated)
        {
            ToastService.Show(
                $"Stopped at {JsonArrayColumnDiscovery.MaxColumns} columns — collapse a column to see the rest.");
        }
    }

    private void PublishShape()
    {
        // A re-shape moves which value a column holds, and the pane is labelled with a column's
        // route - so what it is showing may no longer be what its title says.
        CloseCellDetail();

        // Nothing to publish before LoadAsync has discovered a shape - and nothing calls this
        // that early, which is what makes the null a guard rather than a case to handle.
        if (this.structure is not { } shape)
            return;

        this.rows?.SetShape(shape, this.routes, this.mode);
        this.toolbar?.ShowArrayColumns(HasArrayColumns);

        OnPropertyChanged(nameof(Headers));
        OnPropertyChanged(nameof(Structure));
        OnPropertyChanged(nameof(ColumnCount));
        OnPropertyChanged(nameof(RowCount));
    }

    /// <summary>Headers for columns that are not routes into an element - the reshape modes'
    /// generic labels, which nothing can be expanded from.</summary>
    private static IReadOnlyList<JsonArrayColumnHeader> PlainHeaders(CsvStructure structure)
    {
        var plain = new JsonArrayColumnHeader[structure.ColumnCount];
        for (int c = 0; c < plain.Length; c++)
        {
            string name = structure.Columns[c].Name;
            plain[c] = new JsonArrayColumnHeader([new JsonArrayColumnHeaderSegment(name, null)], name);
        }

        return plain;
    }

    /// <summary>
    /// Generic "Column N" labels, widthed from the sampled elements' OWN token lengths bucketed
    /// by <c>i % columns</c> - here the element is exactly one cell, so its own length is the
    /// right measure. No file read and no decoded text: <see cref="JsonTokenInfo.Length"/> is
    /// unpacked O(1) from the token log, which is why re-widthing for a new N costs a walk over
    /// the sample and needs no cache. (An array of objects reshaped this way measures 1 per
    /// element - the brace - and lands on the minimum width, which is the honest result for
    /// cells that show a container summary.)
    /// </summary>
    private CsvStructure BuildReshapeStructure(JsonArrayTableSession current, int columns)
    {
        var index = current.Inner.Index;
        int sample = Math.Min(current.Elements.ElementCount, InitialElementTarget);

        var names = new string[columns];
        var maxChars = new int[columns];
        for (int c = 0; c < columns; c++)
        {
            names[c] = $"Column {c + 1}";
            maxChars[c] = names[c].Length;
        }

        for (int e = 0; e < sample; e++)
        {
            int token = current.Elements.TokenForElement(e);
            int column = e % columns;
            maxChars[column] = Math.Max(maxChars[column], RenderedLength(token, index.GetToken(token)));
        }

        return CsvStructure.FromMaxChars(names, maxChars);
    }

    /// <summary>
    /// Characters the cell for this token will actually render. A scalar's raw token length is
    /// that already (quotes included, which the cell shows), but a container's is the brace
    /// alone - one character - while the cell shows a summary like <c>{ 6 members }</c>. Widthing
    /// a column of nested objects from the brace is what left every container column at the
    /// minimum width, trimmed to "{ 6 mem...", so containers are measured from the summary
    /// itself. It is built here, for a bounded sample, and thrown away.
    /// </summary>
    private int RenderedLength(int tokenIndex, JsonTokenInfo token)
        => IsContainer(token.Kind) && this.cellText is { } factory
            ? factory.BuildContainerSummary(tokenIndex, token, expanded: false).Length
            : token.Length;

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    /// <summary>Marshals background progress reports onto the status line - the same shape as
    /// the shell's StatusProgressReporter and JsonDiffViewModel's: Post (never blocking), ~5%
    /// buckets, silent once the view model is disposed.</summary>
    private sealed class ProgressToStatus : IProgressReporter
    {
        private readonly JsonArrayTableViewModel owner;
        private readonly string label;
        private int lastBucket = -1;

        public ProgressToStatus(JsonArrayTableViewModel owner, string label)
        {
            this.owner = owner;
            this.label = label;
        }

        public void Report(string message, long? current = null, long? max = null)
        {
            string text = this.label;
            if (current.HasValue && max.HasValue && max.Value > 0)
            {
                int percent = (int)Math.Min(100, current.Value * 100L / max.Value);
                int bucket = percent / 5;
                if (bucket == this.lastBucket)
                    return;

                this.lastBucket = bucket;
                text += $"… ({percent}%)";
            }

            ProgressPost.ToUiThread(() =>
            {
                if (!this.owner.IsDisposed)
                    this.owner.StatusText = text;
            });
        }
    }
}
