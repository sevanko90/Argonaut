using Argonaut.Engine.Indexing.Trees;
using Argonaut.Tests.Support;
using Node = Argonaut.Tests.Support.SExpressionTreeFormat.Node;

namespace Argonaut.Tests.Engine.Indexing.Trees;

/// <summary>
/// The generic sparse index, driven by the test-only S-expression format and checked against a
/// whole-document model of the same bytes. Sizes are tiny so a few KB of document exercise
/// promotion, checkpoints and deep nesting.
/// </summary>
public class SparseContainerIndexTests
{
    private const int Promotion = 64;
    private const int Checkpoint = 16;

    /// <summary>The most bytes a parser resuming at a returned point reads to reach its target,
    /// not counting recorded children it skips by their end: up to the promotion size before a
    /// container's first checkpoint, then a checkpoint interval, then one small child.</summary>
    private const int ResumeBound = 2 * Promotion + Checkpoint;

    public static TheoryData<int> Seeds => new() { 1, 2, 3, 4, 5, 6 };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RecordsExactlyTheContainersThatReachThePromotionSize(int seed)
    {
        var (document, nodes, index) = Build(seed);
        var expected = LargeLists(nodes);

        Assert.True(expected.Count > 5, "the document should have several large containers");
        Assert.Equal(expected.Count, index.ContainerCount);
        for (int record = 0; record < expected.Count; record++)
        {
            var node = nodes[expected[record]];
            var actual = index.GetContainer(record);
            Assert.Equal(node.Start, actual.Start);
            Assert.Equal(node.End, actual.End);
            Assert.Equal(node.Depth, actual.Depth);
            Assert.Equal(node.Ordinal, actual.OrdinalInParent);
            Assert.Equal(node.Children.Count, actual.ChildCount);
            Assert.Equal(node.Parent < 0 ? -1 : expected.IndexOf(node.Parent), actual.Parent);
            Assert.Equal(SExpressionTreeFormat.ListKind, actual.FormatKind);
        }

        Assert.True(index.IsComplete);
        Assert.Equal(document.Length, index.ScannedTo);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryCheckpointIsAChildStartOfItsContainerAndTheyAreSpaced(int seed)
    {
        var (_, nodes, index) = Build(seed);
        var records = LargeLists(nodes);

        Assert.True(index.CheckpointCount > 20);
        var lastPerContainer = new Dictionary<int, long>();
        for (int i = 0; i < index.CheckpointCount; i++)
        {
            var checkpoint = index.GetCheckpoint(i);
            var container = nodes[records[checkpoint.Container]];
            var child = nodes[container.Children[(int)checkpoint.Ordinal]];
            Assert.Equal(child.Start, checkpoint.Offset);

            if (lastPerContainer.TryGetValue(checkpoint.Container, out long previous))
                Assert.True(checkpoint.Offset - previous >= Checkpoint);
            else
                Assert.True(checkpoint.Offset - container.Start >= Checkpoint);
            lastPerContainer[checkpoint.Container] = checkpoint.Offset;

            if (i > 0)
                Assert.True(checkpoint.Offset > index.GetCheckpoint(i - 1).Offset);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void FindResumePoint_ByOffset_StartsInTheInnermostContainerCloseEnoughToTheTarget(int seed)
    {
        var (document, nodes, index) = Build(seed);
        var records = LargeLists(nodes);
        var innermost = InnermostRecorded(document.Length, nodes, records);

        for (long target = 0; target < document.Length; target++)
        {
            var point = index.FindResumePoint(target);

            Assert.Equal(innermost[target], point.Container);
            Assert.Equal(innermost[target], index.FindInnermostContainer(target));
            if (point.Container < 0)
                continue;

            AssertValidResumePoint(point, nodes, records);
            Assert.True(point.Offset <= target);
            Assert.True(UnskippableBytes(point, target, nodes, records) <= ResumeBound,
                $"resuming at {point.Offset} for {target} reads too far");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void FindResumePoint_ByOrdinal_StartsAtOrBeforeTheChildAndCloseToIt(int seed)
    {
        var (_, nodes, index) = Build(seed);
        var records = LargeLists(nodes);

        for (int record = 0; record < records.Count; record++)
        {
            var container = nodes[records[record]];
            for (int ordinal = 0; ordinal < container.Children.Count; ordinal++)
            {
                var point = index.FindResumePoint(record, ordinal);

                Assert.Equal(record, point.Container);
                AssertValidResumePoint(point, nodes, records);
                Assert.True(point.Ordinal <= ordinal);

                long childStart = nodes[container.Children[ordinal]].Start;
                Assert.True(UnskippableBytes(point, childStart, nodes, records) <= ResumeBound,
                    $"resuming at ordinal {point.Ordinal} for {ordinal} reads too far");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AnUnfinishedScanRecordsOpenContainersAndResumesWithinWhatItHasSeen(int seed)
    {
        byte[] document = SExpressionTreeFormat.Generate(new Random(seed), topLevelChildren: 60);
        var nodes = SExpressionTreeFormat.Parse(document);
        int stop = document.Length * 2 / 3;
        var index = new SparseContainerIndex(Promotion, Checkpoint);
        SExpressionTreeFormat.Scan(document, new SparseContainerIndexBuilder(index), stopAt: stop);

        // What the index should hold having seen only the first `stop` bytes: every list whose
        // seen part reached the promotion size, open or closed.
        var seen = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.IsList && node.Start < stop && Math.Min(node.End, stop) - node.Start >= Promotion)
                seen.Add(i);
        }

        Assert.False(index.IsComplete);
        Assert.Equal(stop, index.ScannedTo);
        Assert.Equal(seen.Count, index.ContainerCount);
        Assert.Contains(seen, i => nodes[i].End > stop);
        for (int record = 0; record < seen.Count; record++)
        {
            var node = nodes[seen[record]];
            var actual = index.GetContainer(record);
            Assert.Equal(node.Start, actual.Start);
            Assert.Equal(node.End <= stop ? node.End : -1, actual.End);
            Assert.Equal(node.End <= stop ? node.Children.Count : -1, actual.ChildCount);
        }

        for (long target = 0; target < stop; target++)
        {
            var point = index.FindResumePoint(target);
            if (point.Container >= 0)
            {
                AssertValidResumePoint(point, nodes, seen);
                Assert.True(point.Offset <= target);
            }
        }
    }

    [Fact]
    public void TopLevelValuesAreNumberedInTheirOwnSequence()
    {
        byte[] document = "(a) b (cccccccccccccccccccccc dddddddddddddddddddddddd eeeeeeeeeeeeeeeeeeeeeeeeeee)"u8.ToArray();
        var index = new SparseContainerIndex(Promotion, Checkpoint);

        SExpressionTreeFormat.Scan(document, new SparseContainerIndexBuilder(index));

        var only = Assert.Single(Enumerable.Range(0, index.ContainerCount).Select(index.GetContainer));
        Assert.Equal(2, only.OrdinalInParent);
        Assert.Equal(-1, only.Parent);
        Assert.Equal(3, only.ChildCount);
    }

    private static (byte[] Document, List<Node> Nodes, SparseContainerIndex Index) Build(int seed)
    {
        byte[] document = SExpressionTreeFormat.Generate(new Random(seed), topLevelChildren: 60);
        var index = new SparseContainerIndex(Promotion, Checkpoint);
        SExpressionTreeFormat.Scan(document, new SparseContainerIndexBuilder(index));
        return (document, SExpressionTreeFormat.Parse(document), index);
    }

    /// <summary>Node indices of the lists the index should record, in start order - which is
    /// record order.</summary>
    private static List<int> LargeLists(List<Node> nodes)
    {
        var large = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].IsList && nodes[i].End - nodes[i].Start >= Promotion)
                large.Add(i);
        }

        return large;
    }

    /// <summary>For each offset, the record of the innermost recorded list enclosing it, or -1.
    /// Records are painted in start order, so a descendant paints over its ancestor.</summary>
    private static int[] InnermostRecorded(int length, List<Node> nodes, List<int> records)
    {
        var innermost = new int[length];
        Array.Fill(innermost, -1);
        for (int record = 0; record < records.Count; record++)
        {
            var node = nodes[records[record]];
            for (long offset = node.Start; offset < node.End; offset++)
                innermost[offset] = record;
        }

        return innermost;
    }

    private static void AssertValidResumePoint(TreeResumePoint point, List<Node> nodes, List<int> records)
    {
        var container = nodes[records[point.Container]];
        if (point.AtOpen)
        {
            Assert.Equal(container.Start, point.Offset);
            Assert.Equal(0, point.Ordinal);
            return;
        }

        // A child start, or the container's end when the resume point follows its last child.
        if (point.Ordinal == container.Children.Count)
            Assert.True(point.Offset < container.End || container.End < 0);
        else
            Assert.Equal(nodes[container.Children[(int)point.Ordinal]].Start, NextNonWhitespace(point, nodes, container));
    }

    /// <summary>A resume point is a child's start or the end of the child before it, which may be
    /// followed by whitespace; the model only knows starts.</summary>
    private static long NextNonWhitespace(TreeResumePoint point, List<Node> nodes, Node container)
    {
        long childStart = nodes[container.Children[(int)point.Ordinal]].Start;
        long previousEnd = point.Ordinal == 0 ? container.Start + 1 : nodes[container.Children[(int)point.Ordinal - 1]].End;
        Assert.InRange(point.Offset, previousEnd, childStart);
        return childStart;
    }

    /// <summary>Bytes between the resume point and the target that a parser actually reads:
    /// everything except the recorded children of the resume container it can skip by their
    /// recorded end.</summary>
    private static long UnskippableBytes(TreeResumePoint point, long target, List<Node> nodes, List<int> records)
    {
        var container = nodes[records[point.Container]];
        long skipped = 0;
        foreach (int child in container.Children)
        {
            var node = nodes[child];
            if (node.Start >= target)
                break;
            if (node.IsList && node.End - node.Start >= Promotion && node.Start >= point.Offset && node.End <= target)
                skipped += node.End - node.Start;
        }

        return target - point.Offset - skipped;
    }
}
