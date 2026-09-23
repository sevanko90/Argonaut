using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Where <see cref="RawEditOverviewMarks"/> puts an edit on the strip beside the scrollbar. The
/// strip has to agree with the scrollbar, so marks are placed by row - the long-line case is the
/// one where a byte scale would put them visibly in the wrong place.
/// </summary>
public class RawEditOverviewTests
{
    private const int Height = 100;

    private sealed class Edited
    {
        public Edited(string text, int wrapWidth = 80)
        {
            var source = new MemoryByteSource(Encoding.UTF8.GetBytes(text));
            var index = RawSegmentIndex.StartIndexing(source, wrapWidth);
            index.IndexingTask.GetAwaiter().GetResult();
            Table = new RawPieceTable(source);
            Rows = new RawEditedRowIndex(index, Table);
        }

        public RawPieceTable Table { get; }
        public RawEditedRowIndex Rows { get; }

        public void Insert(long offset, string text) => Rows.ApplyEdit(Table.Insert(offset, Encoding.UTF8.GetBytes(text)));

        public void Delete(long offset, long length) => Rows.ApplyEdit(Table.Delete(offset, length));

        public List<RawEditMark> Marks(int height = Height)
        {
            var marks = new List<RawEditMark>();
            RawEditOverviewMarks.Compute(Table, Rows, height, marks);
            return marks;
        }

        public long LineStart(int line) => Rows.GetRowInfo(line).Start; // one row per line below
    }

    private static string Lines(int count)
    {
        var text = new StringBuilder();
        for (int i = 0; i < count; i++)
            text.Append($"line {i:D5}\n");

        return text.ToString();
    }

    [Fact]
    public void AnUneditedDocument_HasNoMarks()
        => Assert.Empty(new Edited(Lines(1000)).Marks());

    [Fact]
    public void AnInsert_IsMarkedAtItsRowsPlaceInTheDocument()
    {
        var document = new Edited(Lines(1000));
        long at = document.LineStart(500) + 3;
        document.Insert(at, "typed");

        var mark = Assert.Single(document.Marks());
        Assert.Equal(50, mark.Top);
        Assert.Equal(at, mark.Offset);
    }

    [Fact]
    public void ADelete_IsMarkedToo()
    {
        // A deletion leaves no inserted bytes, only a seam between two original pieces - the case
        // that reading the scratch pieces alone would miss.
        var document = new Edited(Lines(1000));
        long at = document.LineStart(250) + 2;
        document.Delete(at, 4);

        var mark = Assert.Single(document.Marks());
        Assert.Equal(25, mark.Top);
        Assert.Equal(at, mark.Offset);
    }

    [Fact]
    public void DeletingTheTail_IsMarkedAtTheBottom()
    {
        var document = new Edited(Lines(1000));
        document.Delete(document.LineStart(990), document.Table.AvailableLength - document.LineStart(990));

        Assert.Equal(Height - 1, Assert.Single(document.Marks()).Top);
    }

    [Fact]
    public void Marks_ArePlacedByRowNotByByte()
    {
        // 400KB in one line is 5,000 rows at W=80, then 5,000 one-row lines of 11 bytes. Line
        // 2,500 of those is row 7,500 of 10,000 - three quarters down the scrollbar - but ~94% of
        // the way through the bytes.
        var document = new Edited(new string('a', 400_000) + "\n" + Lines(5000));
        long at = document.Rows.GetRowInfo(7_501).Start;
        document.Insert(at, "z");

        Assert.Equal(75, Assert.Single(document.Marks()).Top);
    }

    [Fact]
    public void EditsInTheSamePixel_BecomeOneMark_AndSeparatePixelsStaySeparate()
    {
        var document = new Edited(Lines(1000));
        document.Insert(document.LineStart(100), "a");
        document.Insert(document.LineStart(101), "b");   // same pixel: 10 rows a pixel
        document.Insert(document.LineStart(800), "c");

        var marks = document.Marks();
        Assert.Equal(2, marks.Count);
        Assert.Equal(10, marks[0].Top);
        Assert.Equal(80, marks[1].Top);
    }

    [Fact]
    public void EditingEveryLine_StillMakesAtMostOneMarkPerPixel()
    {
        var document = new Edited(Lines(2000));
        for (int line = 1999; line >= 0; line -= 3)
            document.Insert(document.LineStart(line), "x");

        var marks = document.Marks(height: 50);
        Assert.InRange(marks.Count, 1, 50);
        Assert.All(marks.Zip(marks.Skip(1)), pair => Assert.True(pair.First.Bottom < pair.Second.Top));
    }

    [Fact]
    public void ALargePaste_SpansThePixelsItsRowsCover()
    {
        var document = new Edited(Lines(1000));
        document.Insert(document.LineStart(200), Lines(1000));   // 2,000 rows now; paste is 200..1199

        var mark = Assert.Single(document.Marks());
        Assert.Equal(10, mark.Top);
        Assert.Equal(59, mark.Bottom);
    }

    [Fact]
    public void DeletingEverything_LeavesOneMarkAtTheTop()
    {
        var document = new Edited(Lines(10));
        document.Delete(0, document.Table.AvailableLength);

        Assert.Equal(new RawEditMark(0, 0, 0), Assert.Single(document.Marks()));
    }

    [Fact]
    public void UndoingBackToTheOriginal_ClearsTheMarks()
    {
        var document = new Edited(Lines(1000));
        long at = document.LineStart(400);
        document.Insert(at, "temporary");
        document.Delete(at, "temporary".Length);

        Assert.Empty(document.Marks());
    }
}
