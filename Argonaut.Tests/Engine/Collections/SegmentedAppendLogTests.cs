using Argonaut.Engine.Collections;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Raw.Rows;
using Argonaut.Features.Xml.Indexing;

namespace Argonaut.Tests.Engine.Collections;

/// <summary>
/// Covers the lock-free segmented log both indexers publish through: value fidelity across
/// segment boundaries and table growth, bounds behavior, post-publication field mutation,
/// and a single-writer/concurrent-reader smoke test of the volatile-count publication
/// contract (readers only ever touch indices below an observed Count).
/// </summary>
public class SegmentedAppendLogTests
{
    private struct Item
    {
        public int Value;
        public int Mutable;
    }

    [Fact]
    public void Add_ReturnsSequentialIndices_AndCountTracks()
    {
        var log = new SegmentedAppendLog<int>();
        Assert.Equal(0, log.Count);
        Assert.Equal(0, log.Add(10));
        Assert.Equal(1, log.Add(20));
        Assert.Equal(2, log.Count);
        Assert.Equal(10, log.ItemRef(0));
        Assert.Equal(20, log.ItemRef(1));
    }

    [Fact]
    public void ItemsSurviveSegmentBoundariesAndTableGrowth()
    {
        // 300,000 ints crosses many 2,048-item segment boundaries and forces the initial
        // 16-slot segment table to grow (16 * 2,048 = 32,768) several times.
        const int n = 300_000;
        var log = new SegmentedAppendLog<int>();
        for (int i = 0; i < n; i++)
            log.Add(i);

        Assert.Equal(n, log.Count);
        for (int i = 0; i < n; i++)
            Assert.Equal(i, log.ItemRef(i));
    }

    [Fact]
    public void ItemRef_OutOfRange_Throws()
    {
        var log = new SegmentedAppendLog<int>();
        log.Add(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ItemRef(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ItemRef(-1));
    }

    [Fact]
    public void RefsRemainValidAcrossLaterGrowth_AndSupportMutation()
    {
        var log = new SegmentedAppendLog<Item>();
        int early = log.Add(new Item { Value = 42, Mutable = -1 });

        // Growth swaps the segment table but never moves segments, so a ref taken before
        // growth must still address the live item afterwards.
        for (int i = 0; i < 200_000; i++)
            log.Add(new Item { Value = i });

        log.ItemRef(early).Mutable = 99;
        Assert.Equal(42, log.ItemRef(early).Value);
        Assert.Equal(99, log.ItemRef(early).Mutable);
    }

    [Fact]
    public async Task ConcurrentReaders_OnlyEverSeeFullyPublishedItems()
    {
        // Single writer appends items whose payload encodes their own index; readers
        // hammer indices below whatever Count they observe. Any torn/unpublished read
        // shows up as a payload/index mismatch.
        const int n = 500_000;
        var log = new SegmentedAppendLog<long>();

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < n; i++)
                log.Add(((long)i << 20) ^ i);
        });

        var readers = Enumerable.Range(0, 3).Select(seed => Task.Run(() =>
        {
            var rng = new Random(seed);
            while (true)
            {
                int count = log.Count;
                if (count > 0)
                {
                    int i = rng.Next(count);
                    long value = log.ItemRef(i);
                    Assert.Equal(((long)i << 20) ^ i, value);
                }

                if (count == n)
                    return;
            }
        })).ToArray();

        await writer;
        await Task.WhenAll(readers);
        Assert.Equal(n, log.Count);
    }

    // Every index session pays one segment per log on its first append, however small the
    // document. Sized by items (8,192), a ContainerSlot segment was 384 KiB on the large object
    // heap; sized by bytes, every log's segment must stay a gen-0 allocation.
    private const int LargeObjectThreshold = 85_000;

    [Fact]
    public void EveryIndexLogsSegment_StaysOffTheLargeObjectHeap()
    {
        int[] segments =
        [
            SegmentedAppendLog<SparseContainerIndex.ContainerSlot>.SegmentBytes,
            SegmentedAppendLog<TreeCheckpoint>.SegmentBytes,
            SegmentedAppendLog<FileLineAnchor>.SegmentBytes,
            SegmentedAppendLog<RawRowAnchor>.SegmentBytes,
            SegmentedAppendLog<long>.SegmentBytes,
            XmlSiblingCounts.EntrySegmentBytes,
        ];

        Assert.All(segments, bytes => Assert.InRange(bytes, 8 * 1024, 16 * 1024));
        Assert.All(segments, bytes => Assert.True(bytes < LargeObjectThreshold));
    }

    [Fact]
    public void TheFirstAppend_AllocatesOneSmallSegment()
    {
        // Warm the JIT and the type's statics so only the log's own allocation is measured.
        new SegmentedAppendLog<SparseContainerIndex.ContainerSlot>().Add(default);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var log = new SegmentedAppendLog<SparseContainerIndex.ContainerSlot>();
        log.Add(default);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, SegmentedAppendLog<SparseContainerIndex.ContainerSlot>.SegmentBytes, 16 * 1024);
    }
}
