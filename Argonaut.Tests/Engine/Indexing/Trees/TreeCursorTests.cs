using Argonaut.Engine.Indexing.Trees;
using Argonaut.Tests.Support;
using Node = Argonaut.Tests.Support.SExpressionTreeFormat.Node;

namespace Argonaut.Tests.Engine.Indexing.Trees;

/// <summary>
/// The generic cursor over the test-only S-expression format, against the row list a whole-document
/// model gives for the same expansion. Tiny promotion and checkpoint sizes, so stepping and seeking
/// go through recorded containers, checkpoints and resume points at every depth.
/// </summary>
public class TreeCursorTests
{
    public static TheoryData<int, int> Cases => new()
    {
        { 1, 0 }, { 2, 1 }, { 3, 2 }, { 4, 3 }, { 5, 9 }, { 6, 1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void WalkingForwardGivesEveryRowInOrder(int seed, int defaultDepth)
    {
        var fixture = Build(seed, defaultDepth);
        var cursor = fixture.Cursor();

        var walked = new List<(long, bool, long, int, long)>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            walked.Add(Describe(cursor.Current));

        Assert.Equal(fixture.Rows, walked);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WalkingBackwardGivesEveryRowInReverse(int seed, int defaultDepth)
    {
        var fixture = Build(seed, defaultDepth);
        var cursor = fixture.Cursor();

        var walked = new List<(long, bool, long, int, long)>();
        for (bool more = cursor.MoveToEnd(); more; more = cursor.MovePrevious())
            walked.Add(Describe(cursor.Current));
        walked.Reverse();

        Assert.Equal(fixture.Rows, walked);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SeekingLandsOnTheRowShowingTheOffsetAndStepsFromThere(int seed, int defaultDepth)
    {
        var fixture = Build(seed, defaultDepth);
        var cursor = fixture.Cursor();

        for (long offset = 0; offset < fixture.Document.Length; offset += 3)
        {
            Assert.True(cursor.SeekTo(offset));
            int expected = fixture.RowShowing(offset);
            Assert.Equal(fixture.Rows[expected], Describe(cursor.Current));

            if (expected + 1 < fixture.Rows.Count)
            {
                Assert.True(cursor.MoveNext());
                Assert.Equal(fixture.Rows[expected + 1], Describe(cursor.Current));
                cursor.SeekTo(offset);
            }

            if (expected > 0)
            {
                Assert.True(cursor.MovePrevious());
                Assert.Equal(fixture.Rows[expected - 1], Describe(cursor.Current));
            }
        }
    }

    [Fact]
    public void AnEmptyDocumentHasNoRows()
    {
        byte[] document = "   "u8.ToArray();
        var index = new SparseContainerIndex(64, 16);
        SExpressionTreeFormat.Scan(document, new SparseContainerIndexBuilder(index));
        var cursor = new TreeCursor(index, new SExpressionTreeFormat.Reader(document), new TreeExpandState(1));

        Assert.False(cursor.MoveToStart());
        Assert.False(cursor.MoveToEnd());
        Assert.False(cursor.SeekTo(1));
    }

    /// <summary>A row as the tests compare it: value start, whether it is a close row, where
    /// its text starts, depth and ordinal.</summary>
    private static (long, bool, long, int, long) Describe(TreeRow row) =>
        (row.Node.ValueStart, row.Shape == TreeRowShape.Close, row.Start, row.Depth, row.Ordinal);

    private sealed class Fixture
    {
        public required byte[] Document { get; init; }
        public required List<Node> Nodes { get; init; }
        public required SparseContainerIndex Index { get; init; }
        public required TreeExpandState Expand { get; init; }
        public required List<(long ValueStart, bool IsClose, long Start, int Depth, long Ordinal)> Rows { get; init; }

        public TreeCursor Cursor() => new(Index, new SExpressionTreeFormat.Reader(Document), Expand);

        /// <summary>The row the cursor should land on for <paramref name="offset"/>: descend
        /// through the first child ending past it, into expanded lists only past their opening;
        /// if no child ends past it, the enclosing list's close row, or at the top the last
        /// row.</summary>
        public int RowShowing(long offset)
        {
            var siblings = Nodes.Select((n, i) => (n, i)).Where(p => p.n.Parent < 0).Select(p => p.i).ToList();
            int? enclosing = null;
            while (true)
            {
                int target = siblings.FirstOrDefault(i => offset < Nodes[i].End, -1);
                if (target < 0)
                {
                    return enclosing is int list
                        ? Rows.IndexOf(Rows.Single(r => r.ValueStart == Nodes[list].Start && r.IsClose))
                        : Rows.Count - 1;
                }

                var node = Nodes[target];
                if (node.IsList && Expand.IsExpanded(node.Start, node.Depth) && offset >= node.Start + 1)
                {
                    enclosing = target;
                    siblings = node.Children;
                    continue;
                }

                return Rows.FindIndex(r => r.ValueStart == node.Start && !r.IsClose);
            }
        }
    }

    private static Fixture Build(int seed, int defaultDepth)
    {
        var random = new Random(seed);
        byte[] document = SExpressionTreeFormat.Generate(random, topLevelChildren: 40);
        var nodes = SExpressionTreeFormat.Parse(document);
        var index = new SparseContainerIndex(promotionBytes: 64, checkpointBytes: 16);
        SExpressionTreeFormat.Scan(document, new SparseContainerIndexBuilder(index));

        var expand = new TreeExpandState(defaultDepth);
        foreach (var node in nodes.Where(n => n.IsList))
        {
            if (random.Next(5) == 0)
                expand.Toggle(node.Start);
        }

        var rows = new List<(long, bool, long, int, long)>();
        void Append(int nodeIndex)
        {
            var node = nodes[nodeIndex];
            rows.Add((node.Start, false, node.Start, node.Depth, node.Ordinal));
            if (!node.IsList || !expand.IsExpanded(node.Start, node.Depth))
                return;

            foreach (int child in node.Children)
                Append(child);
            rows.Add((node.Start, true, node.End - 1, node.Depth, node.Ordinal));
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Parent < 0)
                Append(i);
        }

        return new Fixture { Document = document, Nodes = nodes, Index = index, Expand = expand, Rows = rows };
    }
}
