using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Csv;

public sealed class CsvViewModel : ObservableObject, IDisposable
{
    private const int InitialIndexedRowTarget = 250;

    private IndexedFileSession<FileOffsetIndex>? session;
    private CsvRowCollection? rows;
    private CsvColumnLayout? columnLayout;
    private string[] headerFields = [];
    private byte delimiter;
    private bool isHeaderRow = true;
    private IReadOnlyList<CsvCell> headerCells = [];

    public string FilePath { get; private set; } = string.Empty;

    internal FileOffsetIndex? Index => this.session?.Index;

    public Task IndexingTask => this.session?.IndexingTask ?? Task.CompletedTask;

    public int RowCount => this.rows?.Count ?? 0;

    public CsvRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

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
        // Cancel first so the background line-offset scan stops promptly; the row collection
        // must be disposed before session.Dispose joins the scan and releases the mapping.
        this.session?.Cancel();
        this.rows?.Dispose();
        this.session?.Dispose();
    }
}
