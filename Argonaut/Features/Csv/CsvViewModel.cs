using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.NdJson;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Features.Csv;

public sealed class CsvViewModel : IndexedDocumentViewModel
{
    private const int InitialIndexedRowTarget = 250;

    private IndexedFileSession<FileOffsetIndex>? session;
    private CsvRowCollection? rows;
    private CsvStructure? structure;
    private string[] headerFields = [];
    private byte delimiter;
    private bool isHeaderRow = true;
    private string? highlightTerm;
    private int? selectedRowIndex;
    private int? selectedColumnIndex;

    protected override IDocumentSession? Session => this.session;

    protected override IDisposable? MappedRows => this.rows;

    internal FileOffsetIndex? Index => this.session?.Index;

    internal MMapFile? Mmap => this.session?.File;

    /// <summary>Fires when this document begins tearing down, for
    /// <see cref="ISearchNavigator.DocumentTearingDown"/> - a find reveal links it so it stops
    /// rather than touching a released mapping.</summary>
    internal CancellationToken TearingDown => this.session?.TearingDown ?? default;

    internal byte Delimiter => this.delimiter;

    public int RowCount => this.rows?.Count ?? 0;

    public CsvRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    /// <summary>CSV has no header-region toolbar (no date hints, no tree to expand).</summary>
    public override object? Toolbar => null;

    public CsvStructure Structure => this.structure ?? throw new InvalidOperationException("LoadAsync must complete before Structure is accessed.");

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

            this.rows?.SetDataStartIndex(value ? 1 : 0);
            OnPropertyChanged(nameof(RowCount));
            ApplyColumnNames();
        }
    }

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
    public int? SelectedRowIndex
    {
        get => this.selectedRowIndex;
        private set => SetField(ref this.selectedRowIndex, value);
    }

    /// <summary>Column index a search reveal wants scrolled into view horizontally - set even
    /// when <see cref="SelectedRowIndex"/> is null, since the sticky header can still be
    /// scrolled out of view sideways.</summary>
    public int? SelectedColumnIndex
    {
        get => this.selectedColumnIndex;
        private set => SetField(ref this.selectedColumnIndex, value);
    }

    /// <summary>Used by CsvSearchNavigator to reveal a search match - the view reacts to the
    /// resulting property changes by selecting/scrolling, mirroring JsonViewModel.SelectToken's
    /// verb-method shape for selection state.</summary>
    public void SelectRow(int? rowIndex, int? columnIndex)
    {
        SelectedRowIndex = rowIndex;
        SelectedColumnIndex = columnIndex;
    }

    public async Task LoadAsync(string path, byte delimiter, IProgressReporter? progressReporter = null)
    {
        this.FilePath = path;
        this.delimiter = delimiter;

        var session = IndexedFileSession<FileOffsetIndex>.Start(new MMapFile(path), FileOffsetIndex.StartIndexing, progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't a totally empty grid, and so
        // there's a real sample of rows to drive the one-time column-width heuristic; RowCount
        // then tracks index.LineCount live as indexing continues in the background.
        await session.Index.WaitForLineCountAsync(InitialIndexedRowTarget);

        if (session.Index.Failure is { } failure)
            IndexFailure = failure;

        // Row 0 is the only row decoded at load: it carries the column labels. Every other
        // sampled row is measured from its field SPANS (see MeasureColumns), which is what lets
        // the sample be the whole initial batch instead of a token two rows.
        this.headerFields = session.Index.LineCount > 0
            ? CsvFieldReader.ReadFields(session.File, session.Index.GetLineSpan(0), delimiter)
            : [];

        this.structure = CsvStructure.FromMaxChars(ColumnNames(), MeasureColumns(session));
        this.rows = new CsvRowCollection(session.Index, session.File, delimiter, this.isHeaderRow ? 1 : 0);

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Structure));
        OnPropertyChanged(nameof(ColumnCount));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{path} — {RowCount:N0} rows indexed so far";
        MonitorIndexing();
    }

    public override ISearchNavigator? CreateSearchNavigator() => new CsvSearchNavigator(this);

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
        => StatusText = $"{FilePath} — {RowCount:N0} rows";

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
    private int[] MeasureColumns(IndexedFileSession<FileOffsetIndex> session)
    {
        var maxChars = new int[this.headerFields.Length];
        for (int c = 0; c < maxChars.Length; c++)
            maxChars[c] = this.headerFields[c].Length;

        int sampleCount = Math.Min(session.Index.LineCount, InitialIndexedRowTarget);
        for (int i = 1; i < sampleCount; i++)
        {
            var spans = CsvFieldReader.SplitToSpans(session.File, session.Index.GetLineSpan(i), delimiter, CsvFieldReader.MaxDisplayFields);
            int columns = Math.Min(spans.Length, maxChars.Length);
            for (int c = 0; c < columns; c++)
            {
                if (spans[c].Length > maxChars[c])
                    maxChars[c] = spans[c].Length;
            }
        }

        return maxChars;
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
