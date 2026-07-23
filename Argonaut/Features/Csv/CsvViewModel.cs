using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Features.NdJson;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Features.Csv;

public sealed class CsvViewModel : ObservableObject, IDocumentViewModel
{
    private const int InitialIndexedRowTarget = 250;

    private IndexedFileSession<FileOffsetIndex>? session;
    private CsvRowCollection? rows;
    private CsvColumnLayout? columnLayout;
    private string[] headerFields = [];
    private byte delimiter;
    private bool isHeaderRow = true;
    private IReadOnlyList<CsvCell> headerCells = [];
    private string? highlightTerm;
    private string statusText = string.Empty;
    private int? selectedRowIndex;
    private int? selectedColumnIndex;
    private bool disposed;

    public string FilePath { get; private set; } = string.Empty;

    internal FileOffsetIndex? Index => this.session?.Index;

    internal MMapFile? Mmap => this.session?.File;

    internal byte Delimiter => this.delimiter;

    public Task IndexingTask => this.session?.IndexingTask ?? Task.CompletedTask;

    public int RowCount => this.rows?.Count ?? 0;

    /// <summary>Status-bar line for this document (see <see cref="IDocumentViewModel"/>).</summary>
    public string StatusText
    {
        get => this.statusText;
        private set => SetField(ref this.statusText, value);
    }

    public CsvRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    /// <summary>CSV has no header-region toolbar (no date hints, no tree to expand).</summary>
    public object? Toolbar => null;

    public CsvColumnLayout ColumnLayout => this.columnLayout ?? throw new InvalidOperationException("LoadAsync must complete before ColumnLayout is accessed.");

    /// <summary>
    /// The sticky header row's cells. Row 0's parsed fields when <see cref="IsHeaderRow"/> is
    /// true; generic "Column N" labels (still widthed from the same <see cref="ColumnLayout"/>)
    /// when false, so the grid always has a consistent frame regardless of the tickbox.
    /// </summary>
    public IReadOnlyList<CsvCell> HeaderCells
    {
        get => this.headerCells;
        private set => SetField(ref this.headerCells, value);
    }

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
            UpdateHeaderCells();
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

        this.headerFields = session.Index.LineCount > 0
            ? CsvFieldReader.ReadFields(session.File, session.Index.GetLineSpan(0), delimiter)
            : [];

        int sampleCount = Math.Min(session.Index.LineCount, InitialIndexedRowTarget);
        var sampleRows = new List<string[]>(Math.Max(0, sampleCount - 1));
        for (int i = 1; i < sampleCount; i++)
            sampleRows.Add(CsvFieldReader.ReadFields(session.File, session.Index.GetLineSpan(i), delimiter));

        this.columnLayout = CsvColumnLayout.Compute(this.headerFields, sampleRows);
        this.rows = new CsvRowCollection(session.Index, session.File, delimiter, this.columnLayout, this.isHeaderRow ? 1 : 0);
        UpdateHeaderCells();

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(ColumnLayout));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{path} — {RowCount:N0} rows indexed so far";
        _ = MonitorIndexingAsync(session);
    }

    public ISearchNavigator CreateSearchNavigator() => new CsvSearchNavigator(this);

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    public bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Csv || fileType == FileTypeDetector.FileKind.Tsv;
    }

    /// <summary>
    /// Refreshes <see cref="StatusText"/> when background indexing finishes or fails.
    /// Fire-and-forget from LoadAsync (UI thread); the await resumes there per the app's
    /// threading convention. The disposed check covers cancellation-by-dispose: a
    /// superseded or closed document must not repaint its status as a failure.
    /// </summary>
    private async Task MonitorIndexingAsync(IndexedFileSession<FileOffsetIndex> session)
    {
        try
        {
            await session.IndexingTask;
        }
        catch
        {
            if (!this.disposed)
                StatusText = $"{FilePath} — indexing failed";
            return;
        }

        if (!this.disposed)
            StatusText = $"{FilePath} — {RowCount:N0} rows";
    }

    private void UpdateHeaderCells()
    {
        if (this.columnLayout is null)
            return;

        if (this.isHeaderRow)
        {
            var cells = new CsvCell[this.headerFields.Length];
            for (int c = 0; c < cells.Length; c++)
                cells[c] = new CsvCell(this.headerFields[c], this.columnLayout.WidthFor(c));
            HeaderCells = cells;
        }
        else
        {
            var cells = new CsvCell[this.columnLayout.ColumnCount];
            for (int c = 0; c < cells.Length; c++)
                cells[c] = new CsvCell($"Column {c + 1}", this.columnLayout.WidthFor(c));
            HeaderCells = cells;
        }
    }

    public void Dispose()
    {
        // Idempotent - see IDocumentViewModel's lifetime contract.
        if (this.disposed)
            return;
        this.disposed = true;

        // Cancel first so the background line-offset scan stops promptly; the row collection
        // must be disposed before session.Dispose joins the scan and releases the mapping.
        this.session?.Cancel();
        this.rows?.Dispose();
        this.session?.Dispose();
    }
}
