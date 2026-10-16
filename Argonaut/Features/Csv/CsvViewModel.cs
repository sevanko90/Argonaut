using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Selection;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Documents.Numbering;
using Argonaut.Ui.Find;
using Argonaut.Ui.TableGrid;

namespace Argonaut.Features.Csv;

public sealed class CsvViewModel : IndexedDocumentViewModel, IByteRangeNavigable
{
    private const int InitialIndexedRowTarget = 250;

    /// <summary>How much of the whole file an excerpt reads looking for the header line.</summary>
    private const int HeaderReadLimit = 1024 * 1024;

    private IndexedSourceSession<FileOffsetIndex>? session;

    // What the line index was built over, so a finished one is kept for the next view of the
    // same bytes - this one reopened, or NDJSON's, which shares it (see KeptIndexes).
    private IndexBasis? indexBasis;
    private CsvRowCollection? rows;
    private TableStructure? structure;
    private string[] headerFields = [];

    // True when headerFields came from the whole file's first line rather than this document's:
    // an excerpt that starts past line 1 has no header of its own, so every line is data.
    private bool headerFromWhole;
    private byte delimiter;
    private bool isHeaderRow = true;
    private string? highlightTerm;
    private int? selectedRowIndex;
    private int? selectedColumnIndex;
    private int? selectedMatchOccurrence;

    // The data row the grid has selected, however it got there - a click, the keyboard, a reveal.
    // Kept apart from selectedRowIndex, which is a reveal the view acts on: this one nothing acts
    // on, it is only where the user is, for a view switch or a filter reload to carry over.
    private int? rowSelectedInGrid;

    // The data rows the grid has on screen, as the view last reported them - where the document is
    // when nothing selected is in sight. Null until the grid has laid out a row.
    private (int First, int Last)? rowsOnScreen;
    private int? rowShownAtTop;

    private readonly LineNumberingPreference? lineNumbers;

    /// <param name="lineNumbers">How an excerpt numbers its lines; null numbers from the start.</param>
    public CsvViewModel(LineNumberingPreference? lineNumbers = null)
    {
        this.lineNumbers = lineNumbers;
    }

    protected override IDocumentSession? Session => this.session;

    protected override IDisposable? MappedRows => this.rows;

    internal FileOffsetIndex? Index => this.session?.Index;

    internal IByteSource? Bytes => this.session?.Bytes;

    /// <summary>Fires when this document begins tearing down, for
    /// <see cref="ISearchNavigator.DocumentTearingDown"/> - a find reveal links it so it stops
    /// rather than touching a released mapping.</summary>
    internal CancellationToken TearingDown => this.session?.TearingDown ?? default;

    internal byte Delimiter => this.delimiter;

    public int RowCount => this.rows?.Count ?? 0;

    public CsvRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    /// <summary>CSV has no header-region toolbar (no date hints, no tree to expand).</summary>
    public override object? Toolbar => null;

    public TableStructure Structure => this.structure ?? throw new InvalidOperationException("LoadAsync must complete before Structure is accessed.");

    /// <summary>Columns discovered so far - 0 until <see cref="LoadAsync"/> has published a
    /// <see cref="Structure"/>, which is what the view waits for before building columns.</summary>
    public int ColumnCount => this.structure?.ColumnCount ?? 0;

    /// <summary>"First row is header" tickbox. Toggling it doesn't re-read the file - it just
    /// shifts which absolute line <see cref="Rows"/> treats as its first data row.</summary>
    public bool IsHeaderRow
    {
        get => this.isHeaderRow;
        set
        {
            if (!SetField(ref this.isHeaderRow, value))
                return;

            this.rows?.SetDataStartIndex(DataStartIndex);
            OnPropertyChanged(nameof(RowCount));
            ApplyColumnNames();
        }
    }

    /// <summary>The line the data rows start at: past the header when the first line is one,
    /// and 0 for an excerpt whose header was taken from the whole file.</summary>
    internal int DataStartIndex => this.isHeaderRow && !this.headerFromWhole ? 1 : 0;

    /// <summary>The active find term, highlighted in every visible cell (header and data) via
    /// CsvView's SearchHighlight bindings. No nested view model to propagate into, unlike
    /// NdJsonViewModel.HighlightTerm.</summary>
    public string? HighlightTerm
    {
        get => this.highlightTerm;
        set => SetField(ref this.highlightTerm, value);
    }

    /// <summary>Virtual row index within <see cref="Rows"/> that a search reveal wants
    /// scrolled/selected into view; null if the current reveal target has no data row (e.g. a
    /// match landed on the header line while <see cref="IsHeaderRow"/> is true).</summary>
    public int? SelectedRowIndex => this.selectedRowIndex;

    /// <summary>Column index a search reveal wants scrolled into view horizontally - set even
    /// when <see cref="SelectedRowIndex"/> is null, since the header can still be scrolled out
    /// of view sideways.</summary>
    public int? SelectedColumnIndex => this.selectedColumnIndex;

    /// <summary>Which occurrence of the find term in the selected cell the reveal landed on, 0 for
    /// the first - so the grid can mark the one find is on among several in a cell; null when the
    /// reveal was not a find match.</summary>
    public int? SelectedMatchOccurrence => this.selectedMatchOccurrence;

    /// <summary>
    /// Used by CsvSearchNavigator to reveal a search match - the view reacts to the resulting
    /// property changes by selecting/scrolling, mirroring JsonViewModel.SelectToken's verb-method
    /// shape for selection state.
    ///
    /// Notifies unconditionally rather than through SetField, because a reveal is an ACTION and
    /// not a state change: with a single match in the file (or one line holding all of them),
    /// next/prev asks for the row that is already selected, and after the user has scrolled away
    /// that still has to scroll back. Gating on inequality made those presses do nothing at all.
    /// </summary>
    public void SelectRow(int? rowIndex, int? columnIndex, int? matchOccurrence = null)
    {
        this.selectedRowIndex = rowIndex;
        this.selectedColumnIndex = columnIndex;
        this.selectedMatchOccurrence = matchOccurrence;

        OnPropertyChanged(nameof(SelectedMatchOccurrence));
        OnPropertyChanged(nameof(SelectedRowIndex));
        OnPropertyChanged(nameof(SelectedColumnIndex));
    }

    /// <summary>The row the view wants put at the top of the screen, not selected - a place to go
    /// back to (<see cref="RevealByteRangeAsync"/>). Notified even when unchanged, like
    /// <see cref="SelectRow"/>: it is an action, not a state.</summary>
    public int? RowShownAtTop => this.rowShownAtTop;

    /// <summary>The grid's selection moved, to <paramref name="rowIndex"/> or to nothing. Only
    /// recorded, for <see cref="SelectedByteRange"/>: the grid already shows it.</summary>
    public void RecordSelectedRow(int? rowIndex) => this.rowSelectedInGrid = rowIndex;

    /// <summary>The grid scrolled, or was laid out: <paramref name="first"/> to
    /// <paramref name="last"/> are on screen, or none when last is before first. Only recorded,
    /// for <see cref="SelectedByteRange"/>.</summary>
    public void RecordRowsOnScreen(int first, int last) => this.rowsOnScreen = last >= first ? (first, last) : null;

    /// <summary>
    /// Where the user is, for a filter reload or a switch to another view to land back on - as raw
    /// does it. The selected row's bytes, without its newline, when it is on screen; otherwise the
    /// start of the top row on screen, as an empty range: a place, not a selection, so the view
    /// coming back scrolls to it and selects nothing (<see cref="RevealByteRangeAsync"/>). Null
    /// when the grid has neither.
    /// </summary>
    public ByteRange? SelectedByteRange
    {
        get
        {
            if (this.session?.Index is not { } index || this.session.Bytes is not { } bytes)
                return null;

            if (this.rowSelectedInGrid is int selected
                && (this.rowsOnScreen is not { } shown || (selected >= shown.First && selected <= shown.Last))
                && selected + DataStartIndex < index.LineCount)
            {
                var span = LineReader.TrimTrailingNewline(bytes, index.GetLineSpan(selected + DataStartIndex));
                return new ByteRange(span.Offset, span.Length);
            }

            if (this.rowsOnScreen is { } screen && screen.First + DataStartIndex < index.LineCount)
                return ByteRange.At(index.GetLineSpan(screen.First + DataStartIndex).Offset);

            return null;
        }
    }

    /// <summary>
    /// Goes back to <paramref name="range"/> once indexing has reached it. A range with bytes in
    /// it is a selection, and selects the row it starts on; an empty one is only a place, and puts
    /// its row at the top of the screen with nothing selected - which is also what a selected row
    /// becomes when a filter reload drops it. The column is left alone, unlike a find reveal:
    /// nothing about a byte range says which one the user was looking at.
    /// </summary>
    public async Task RevealByteRangeAsync(ByteRange range)
    {
        if (this.session?.Index is not { } index || IsDisposed)
            return;

        int? line;
        try
        {
            line = await OffsetLineResolver.ResolveWhenCoveredAsync(index, range.Offset, TearingDown);
        }
        catch (OperationCanceledException)
        {
            return; // the document closed while the line was resolving
        }

        if (line is not int found)
            return;

        int row = Math.Max(0, found - DataStartIndex);
        if (range.Length > 0)
        {
            if (found >= DataStartIndex)
                SelectRow(row, columnIndex: null);
            return;
        }

        this.rowShownAtTop = row;
        OnPropertyChanged(nameof(RowShownAtTop));
    }

    public async Task LoadAsync(IByteOrigin origin, byte delimiter, IProgressReporter? progressReporter = null)
    {
        this.Origin = origin;
        this.FilePath = origin.Path ?? origin.DisplayName;
        FollowLineNumbering(this.lineNumbers);
        this.delimiter = delimiter;

        this.indexBasis = IndexBasis.Of(origin);
        var kept = this.indexBasis?.FindKept<FileLineAnchors>(FileLineAnchors.Key);
        var session = IndexedSourceSession<FileOffsetIndex>.Start(origin.Open(),
            (bytes, progress, stopping) => FileOffsetIndex.StartIndexing(bytes, kept, progress, stopping), progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't a totally empty grid, and so
        // there's a real sample of rows to drive the one-time column-width heuristic; RowCount
        // then tracks index.LineCount live as indexing continues in the background.
        await session.Index.WaitForLineCountAsync(InitialIndexedRowTarget);

        if (session.Index.Failure is { } failure)
            IndexFailure = failure;

        // Row 0 is the only row decoded at load: it carries the column labels. Every other
        // sampled row is measured from its field SPANS (see MeasureColumns), which is what lets
        // the sample be the whole initial batch instead of a token two rows. An excerpt past
        // line 1 has no header line of its own, so it borrows the whole file's.
        var wholeHeader = ReadWholeFileHeader(origin, delimiter);
        this.headerFromWhole = wholeHeader is not null;
        this.headerFields = wholeHeader
            ?? (session.Index.LineCount > 0 ? CsvFieldReader.ReadFields(session.Bytes, session.Index.GetLineSpan(0), delimiter) : []);

        this.structure = TableStructure.FromMaxChars(ColumnNames(), MeasureColumns(session));
        this.rows = new CsvRowCollection(session.Index, session.Bytes, delimiter, DataStartIndex);

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Structure));
        OnPropertyChanged(nameof(ColumnCount));
        OnPropertyChanged(nameof(RowCount));

        StatusText = DocumentStatusLine.Compose(FilePath, $"{RowCount:N0} rows indexed so far");
        MeasureWidestGutterLabel();
        MonitorIndexing();
    }

    public override ISearchNavigator? CreateSearchNavigator() => new CsvSearchNavigator(this);

    /// <summary>
    /// The widest label the line gutter shows, for it to size itself by: as many zeros as the
    /// widest line number, or the widest offset when lines are labelled by where they start (digits
    /// are the same width in the content font). Set when the rows are first shown, when indexing
    /// finishes and when the numbering is flipped - a file still indexing past a power of ten
    /// widens when it is done, as the NDJSON gutter does.
    /// </summary>
    public string WidestGutterLabel
    {
        get => this.widestGutterLabel;
        private set => SetField(ref this.widestGutterLabel, value);
    }

    private string widestGutterLabel = "0";

    private void MeasureWidestGutterLabel()
    {
        if (LineNumbering.WidestLineStartLabel is { } widestStart)
        {
            WidestGutterLabel = widestStart;
            return;
        }

        long widest = LineNumbering.Line(Math.Max(this.session?.Index.LineCount ?? 1, 1));
        WidestGutterLabel = new string('0', widest.ToString(CultureInfo.InvariantCulture).Length);
    }

    /// <summary>The gutter's labels follow the numbering.</summary>
    protected override void OnLineNumberingChanged() => MeasureWidestGutterLabel();

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Csv || fileType == FileTypeDetector.FileKind.Tsv;
    }

    /// <summary>Indexing finished: reports <see cref="RowCount"/> under its real total.</summary>
    protected override void OnIndexingCompleted()
    {
        this.indexBasis?.Keep(FileLineAnchors.Key, this.session?.Index.DetachAnchors());
        StatusText = DocumentStatusLine.Compose(FilePath, $"{RowCount:N0} rows");
        MeasureWidestGutterLabel();
    }

    /// <summary>Indexing stopped early (failure, or cancellation on <paramref name="failure"/> null).</summary>
    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        IndexFailure = failure;
        StatusText = failure is { } f
            ? $"{FilePath} — indexing stopped — {f.ItemsIndexed:N0} rows shown"
            : $"{FilePath} — indexing failed";
    }

    /// <summary>
    /// Per-column maximum field length over the initial indexed batch, measured from field
    /// spans - no decode, no allocation per field, so the wide sample is nearly free. Seeded
    /// with each header label's own length so a header always fits its column.
    ///
    /// A span's Length is raw bytes, including the field's surrounding quotes and its doubled
    /// "" escapes, so it over-counts slightly against the decoded text. Harmless: the width
    /// formula clamps, and saturates well before the difference could matter.
    /// </summary>
    private int[] MeasureColumns(IndexedSourceSession<FileOffsetIndex> session)
    {
        var maxChars = new int[this.headerFields.Length];
        for (int c = 0; c < maxChars.Length; c++)
            maxChars[c] = this.headerFields[c].Length;

        int sampleCount = Math.Min(session.Index.LineCount, InitialIndexedRowTarget);
        for (int i = this.headerFromWhole ? 0 : 1; i < sampleCount; i++)
        {
            var spans = CsvFieldReader.SplitToSpans(session.Bytes, session.Index.GetLineSpan(i), delimiter, CsvFieldReader.MaxDisplayFields);
            int columns = Math.Min(spans.Length, maxChars.Length);
            for (int c = 0; c < columns; c++)
            {
                if (spans[c].Length > maxChars[c])
                    maxChars[c] = spans[c].Length;
            }
        }

        return maxChars;
    }

    /// <summary>
    /// The whole file's first line, split into fields, when <paramref name="origin"/> is an
    /// excerpt or a selection starting past the start of the file - or null. Reads only the start
    /// of the file: a header longer than <see cref="HeaderReadLimit"/> is not a header anyone can
    /// read in a column title, and the columns fall back to their numbers.
    /// </summary>
    private static string[]? ReadWholeFileHeader(IByteOrigin origin, byte delimiter)
    {
        var whole = origin switch
        {
            ExcerptByteOrigin { Range.Offset: > 0 } excerpt => excerpt.Whole,
            SelectionByteOrigin { Selection.IsEmpty: false } selection when selection.Selection.Runs[0].FileOffset > 0 => selection.Whole,
            _ => null,
        };
        if (whole is null)
            return null;

        long length = Math.Min(whole.AvailableLength, HeaderReadLimit);
        var start = whole.OpenRange(0, length);
        try
        {
            if (LineRangeLocator.Locate(start, 1, 1) is not { } header
                || (header.Range.End == length && length < whole.AvailableLength))
                return null;

            return CsvFieldReader.ReadFields(start, new FileLineSpan(header.Range.Offset, (int)header.Range.Length), delimiter);
        }
        finally
        {
            start.Release();
        }
    }

    /// <summary>Row 0's parsed fields when the tickbox says row 0 is a header, generic
    /// "Column N" labels when it doesn't.</summary>
    private string[] ColumnNames()
    {
        if (this.isHeaderRow)
            return this.headerFields;

        var names = new string[this.headerFields.Length];
        for (int c = 0; c < names.Length; c++)
            names[c] = $"Column {c + 1}";

        return names;
    }

    /// <summary>
    /// Relabels the columns after a tickbox toggle. Only the labels change - the widths were
    /// measured from the data, and the rows carry no names - so the realized rows are untouched
    /// and the view just rebuilds its columns from the new structure.
    /// </summary>
    private void ApplyColumnNames()
    {
        if (this.structure is null)
            return;

        this.structure = this.structure.WithNames(ColumnNames());
        OnPropertyChanged(nameof(Structure));
    }
}
