using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies CsvRowCollection: row-number/cell mapping through a dataStartIndex offset (the
/// "first row is header" tickbox), Count adjusting with that offset, and SetDataStartIndex
/// invalidating the cache and raising Reset. Indexing is always run to completion before
/// construction so the growth-timer path (which needs an Avalonia dispatcher) never starts.
/// </summary>
public class CsvRowCollectionTests
{
    private const string Content = "id,name\n1,alpha\n2,beta\n3,gamma\n";

    private static void WithRows(string content, int dataStartIndex, Action<CsvRowCollection> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            var index = FileOffsetIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();

            var header = CsvFieldReader.ReadFields(file, index.GetLineSpan(0), (byte)',');
            var layout = CsvColumnLayout.Compute(header, []);

            using var rows = new CsvRowCollection(index, file, (byte)',', layout, dataStartIndex);
            assert(rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Count_WithHeaderOffset_ExcludesRowZero()
        => WithRows(Content, dataStartIndex: 1, rows => Assert.Equal(3, rows.Count));

    [Fact]
    public void Count_WithoutHeaderOffset_IncludesAllRows()
        => WithRows(Content, dataStartIndex: 0, rows => Assert.Equal(4, rows.Count));

    [Fact]
    public void FirstDataRow_WithHeaderOffset_IsSecondFileLine()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            var row = (CsvVisibleRow)rows[0]!;
            Assert.Equal(1, row.RowNumber);
            Assert.Equal("1", row.Cells[0].Text);
            Assert.Equal("alpha", row.Cells[1].Text);
        });
    }

    [Fact]
    public void FirstRow_WithoutHeaderOffset_IsHeaderLine()
    {
        WithRows(Content, dataStartIndex: 0, rows =>
        {
            var row = (CsvVisibleRow)rows[0]!;
            Assert.Equal("id", row.Cells[0].Text);
            Assert.Equal("name", row.Cells[1].Text);
        });
    }

    [Fact]
    public void CellWidths_ComeFromColumnLayout()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            var row = (CsvVisibleRow)rows[0]!;
            Assert.Equal(60, row.Cells[0].Width); // clamps to the minimum for these short values
        });
    }

    [Fact]
    public void OutOfRangeIndex_ReturnsEmptyRowInsteadOfThrowing()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            var row = (CsvVisibleRow)rows[10]!;
            Assert.Empty(row.Cells);
        });
    }

    [Fact]
    public void SetDataStartIndex_ShiftsWhichLineIsFirstDataRow()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            rows.SetDataStartIndex(0);

            Assert.Equal(4, rows.Count);
            var row = (CsvVisibleRow)rows[0]!;
            Assert.Equal("id", row.Cells[0].Text);
        });
    }

    [Fact]
    public void SetDataStartIndex_RaisesResetNotification()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            System.Collections.Specialized.NotifyCollectionChangedEventArgs? captured = null;
            rows.CollectionChanged += (_, e) => captured = e;

            rows.SetDataStartIndex(0);

            Assert.NotNull(captured);
            Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, captured!.Action);
        });
    }

    [Fact]
    public void SetDataStartIndex_SameValue_DoesNotRaiseNotification()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            bool raised = false;
            rows.CollectionChanged += (_, _) => raised = true;

            rows.SetDataStartIndex(1);

            Assert.False(raised);
        });
    }

    [Fact]
    public void RepeatedAccess_ReturnsCachedRow()
    {
        WithRows(Content, dataStartIndex: 1, rows =>
        {
            var first = rows[0];
            var second = rows[0];
            Assert.Same(first, second);
        });
    }
}
