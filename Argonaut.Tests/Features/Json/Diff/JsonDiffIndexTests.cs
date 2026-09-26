using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Diff;

/// <summary>
/// The headless differ: Merkle short-circuit, runs of unchanged siblings, name-based object
/// matching, merged emission order, trimmed array levels, histogram alignment with in-array moves,
/// identity keys, the in-place comparison of an over-cap middle and its range, and cross-parent
/// move reconciliation including the documented v1 hole (moved-and-edited stays Added/Removed).
/// </summary>
public class JsonDiffIndexTests
{
    internal sealed class DiffFixture : IDisposable
    {
        public JsonDiffIndex Diff { get; private set; } = null!;
        public List<JsonDiffRecord> Records { get; private set; } = null!;
        public string RightPath => paths[1];
        private readonly List<IDisposable> owned = new();
        private readonly List<string> paths = new();

        /// <param name="promotionBytes">Containers at least this long have their hashes and
        /// structure recorded; the default records none of these small documents, 1 records them
        /// all - and with a checkpoint per child, reading one backward walks the index.</param>
        public static async Task<DiffFixture> CreateAsync(string leftJson, string rightJson,
            int promotionBytes = JsonSparseIndex.DefaultPromotionBytes)
        {
            var fixture = new DiffFixture();

            var left = await fixture.IndexAsync(leftJson, promotionBytes);
            var right = await fixture.IndexAsync(rightJson, promotionBytes);

            fixture.Diff = JsonDiffIndex.Start(left, right);
            await fixture.Diff.IndexingTask;

            fixture.Records = new List<JsonDiffRecord>();
            for (int i = 0; i < fixture.Diff.RecordCount; i++)
                fixture.Records.Add(fixture.Diff.GetRecord(i));

            return fixture;
        }

        private async Task<IndexedSourceSession<JsonSparseIndex>> IndexAsync(string json, int promotionBytes)
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path, json, new UTF8Encoding(false));
            paths.Add(path);
            var session = IndexedSourceSession<JsonSparseIndex>.Start(new MMapFile(path),
                (source, progress, stopping) => JsonSparseIndex.StartIndexingWithContentHashes(source, promotionBytes, checkpointBytes: 1, progress, stopping));
            owned.Add(session);
            await session.IndexingTask;
            return session;
        }

        public void Dispose()
        {
            foreach (var d in owned)
                d.Dispose();
            foreach (var p in paths)
                File.Delete(p);
        }
    }

    private static int CountStatus(List<JsonDiffRecord> records, DiffStatus status)
        => records.Count(r => r.Status == status);

    /// <summary>Unchanged pairs across every run.</summary>
    private static long UnchangedPairs(List<JsonDiffRecord> records)
        => records.Where(r => r.IsRun).Sum(r => r.LeftCount);

    private static string Array(IEnumerable<int> values) => "[" + string.Join(',', values) + "]";

    // ── Hash budget ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RecordedAndReadHashes_GiveTheSameDiff(int seed)
    {
        // Recorded hashes and structure (every container, read backward through checkpoints)
        // against hashes and children read from the bytes (none recorded): the record logs must
        // be identical, offsets and all.
        string leftJson = Encoding.UTF8.GetString(Support.RandomJson.LargeContainers(new Random(seed), elements: 30));
        string rightJson = leftJson.Replace("1", "2");

        using var recorded = await DiffFixture.CreateAsync(leftJson, rightJson, promotionBytes: 1);
        using var read = await DiffFixture.CreateAsync(leftJson, rightJson);

        Assert.True(recorded.Records.Count > 1);
        Assert.Equal(read.Records, recorded.Records);
    }

    // ── Merkle short-circuit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task IdenticalDocuments_OneRunOfTheRoot()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":[1,2,{"c":true}]}""",
            """{"a":1,"b":[1,2,{"c":true}]}""");

        var record = Assert.Single(f.Records);
        Assert.True(record.IsRun);
        Assert.Equal(1, record.LeftCount);
        Assert.Equal(new JsonDiffNode(0, 0), record.Left);
        Assert.Equal(new JsonDiffNode(0, 0), record.Right);
        Assert.Equal(-1, record.ParentRecord);
    }

    [Fact]
    public async Task KeyOrderOnlyDifference_OneRun()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":{"x":1,"y":2}}""",
            """{"b":{"y":2,"x":1},"a":1}""");

        Assert.True(Assert.Single(f.Records).IsRun);
    }

    // ── Object levels ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScalarEdit_ModifiedLeafBetweenRuns()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":2,"c":3}""",
            """{"a":1,"b":99,"c":3}""");

        // Root Modified (descended), the run before, the leaf, the run after.
        Assert.Equal(4, f.Records.Count);
        Assert.Equal(DiffStatus.Modified, f.Records[0].Status);
        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Unchanged));
        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Modified));

        var modifiedLeaf = f.Records.Single(r => r.Status == DiffStatus.Modified && r.Index > 0);
        Assert.Equal(1, modifiedLeaf.Depth);
        Assert.Equal(0, modifiedLeaf.ParentRecord);
        Assert.Equal(1, modifiedLeaf.LeftOrdinal);
        Assert.Equal(1, modifiedLeaf.RightOrdinal);
    }

    [Fact]
    public async Task WideObjectWithOneEdit_IsAHandfulOfRecords()
    {
        var members = Enumerable.Range(0, 20_000).Select(i => $"\"k{i}\":{i}").ToList();
        string left = "{" + string.Join(',', members) + "}";
        members[12_345] = "\"k12345\":-1";
        string right = "{" + string.Join(',', members) + "}";

        using var f = await DiffFixture.CreateAsync(left, right);

        Assert.Equal(4, f.Records.Count); // root, run, leaf, run
        Assert.Equal(19_999, UnchangedPairs(f.Records));
        Assert.Equal(12_345, f.Records.Single(r => r.Status == DiffStatus.Modified && r.Index > 0).LeftOrdinal);
    }

    [Fact]
    public async Task AddedAndRemovedKeys_EmittedInMergedOrder()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"gone":2,"z":3}""",
            """{"new":4,"a":1,"z":3}""");

        Assert.Equal(DiffStatus.Modified, f.Records[0].Status);
        var children = f.Records.Where(r => r.ParentRecord == 0).ToList();

        // Merged order: "new" (right-relative position before "a"), "a", removed "gone", "z".
        Assert.Equal(4, children.Count);
        Assert.Equal(DiffStatus.Added, children[0].Status);
        Assert.Equal(DiffStatus.Unchanged, children[1].Status);
        Assert.Equal(DiffStatus.Removed, children[2].Status);
        Assert.Equal(DiffStatus.Unchanged, children[3].Status);
    }

    [Fact]
    public async Task ReorderedKeysAroundAnEdit_SplitRunsWhereTheOrderBreaks()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":2,"c":3,"d":4}""",
            """{"b":2,"a":1,"c":3,"d":5}""");

        // "a" then "b" on the left are right ordinals 1 then 0: not one run.
        var runs = f.Records.Where(r => r.IsRun).ToList();
        Assert.All(runs, r => Assert.Equal(r.LeftCount, r.RightCount));
        Assert.Equal(3, UnchangedPairs(f.Records));
        Assert.True(runs.Count >= 2);
    }

    [Fact]
    public async Task EscapedAndLiteralKeySpellings_Match()
    {
        using var f = await DiffFixture.CreateAsync(
            "{\"caf\\u00e9\":1}",
            "{\"café\":2}");

        // The two spellings decode to one name, so this is a Modified value, not add+remove.
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Modified)); // root + leaf
    }

    [Fact]
    public async Task TypeChange_ModifiedLeafWithoutDescent()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"v":{"a":1}}""",
            """{"v":[1]}""");

        var leaf = f.Records.Single(r => r.Index > 0);
        Assert.Equal(DiffStatus.Modified, leaf.Status);
        Assert.Equal(leaf.Index + 1, leaf.SubtreeEnd); // no descent into mismatched kinds
    }

    // ── Arrays ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAtHeadOfLargeArray_OneAddedAndOneRun()
    {
        string left = Array(Enumerable.Range(0, 1000));
        string right = "[-5," + string.Join(',', Enumerable.Range(0, 1000)) + "]";
        using var f = await DiffFixture.CreateAsync(left, right);

        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Modified)); // only the array itself
        var run = Assert.Single(f.Records, r => r.IsRun);
        Assert.Equal(1000, run.LeftCount);
        Assert.Equal(0, run.LeftOrdinal);
        Assert.Equal(1, run.RightOrdinal);
    }

    [Fact]
    public async Task SwapTwoElements_TwoMovedZeroAddedRemoved()
    {
        using var f = await DiffFixture.CreateAsync("[1,2,3,4]", "[3,2,1,4]");

        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Moved));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));

        // In-array moves carry their source ordinal for the badge.
        var moved = f.Records.Where(r => r.Status == DiffStatus.Moved).ToList();
        Assert.All(moved, r => Assert.True(r.LeftOrdinal >= 0 && r.RightOrdinal >= 0));
        Assert.All(moved, r => Assert.Equal(-1, r.MovePartnerRecord));
    }

    [Fact]
    public async Task ChangedElementInArray_RecursedAsModifiedPair()
    {
        using var f = await DiffFixture.CreateAsync(
            """[{"id":1,"v":"a"},{"id":2,"v":"b"},{"id":3,"v":"c"}]""",
            """[{"id":1,"v":"a"},{"id":2,"v":"CHANGED"},{"id":3,"v":"c"}]""");

        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));

        // The changed element recursed: its "v" member is a Modified leaf, its "id" in a run.
        var leaf = f.Records.Single(r => r.Status == DiffStatus.Modified && r.SubtreeEnd == r.Index + 1);
        Assert.Equal(2, leaf.Depth);
    }

    [Fact]
    public async Task AllIdenticalElements_TrimmedToOneRemoved()
    {
        string left = "[" + string.Join(',', Enumerable.Repeat("0", 500)) + "]";
        string right = "[" + string.Join(',', Enumerable.Repeat("0", 499)) + "]";
        using var f = await DiffFixture.CreateAsync(left, right);

        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(499, UnchangedPairs(f.Records));
    }

    [Fact]
    public async Task AppendToArrayFarOverTheCap_IsExact()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements * 3;
        using var f = await DiffFixture.CreateAsync(
            Array(Enumerable.Range(0, count)),
            Array(Enumerable.Range(0, count + 1)));

        Assert.False(f.Records[0].IsAlignmentApproximate);
        Assert.Equal(3, f.Records.Count); // array, run, added
        Assert.Equal(count, UnchangedPairs(f.Records));
        Assert.Equal(count, Assert.Single(f.Records, r => r.Status == DiffStatus.Added).RightOrdinal);
    }

    [Fact]
    public async Task EditInsideArrayFarOverTheCap_IsExact()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements * 3;
        var edited = Enumerable.Range(0, count).ToArray();
        edited[count / 2] = -1;
        using var f = await DiffFixture.CreateAsync(Array(Enumerable.Range(0, count)), Array(edited));

        Assert.False(f.Records[0].IsAlignmentApproximate);
        Assert.Equal(4, f.Records.Count); // array, run, leaf, run
        Assert.Equal(count / 2, f.Records.Single(r => r.Status == DiffStatus.Modified && r.Index > 0).LeftOrdinal);
    }

    [Fact]
    public async Task EditsAtBothEndsOfAnOverCapArray_ComparedInPlace()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements * 2;
        var edited = Enumerable.Range(0, count).ToArray();
        edited[0] = -1;
        edited[count - 1] = -2;
        using var f = await DiffFixture.CreateAsync(Array(Enumerable.Range(0, count)), Array(edited));

        // Nothing to trim, and a middle far over the cap: compared in place, which still finds
        // exactly the two edits.
        Assert.True(f.Records[0].IsAlignmentApproximate);
        Assert.Equal(3, CountStatus(f.Records, DiffStatus.Modified));
        Assert.Equal(count - 2, UnchangedPairs(f.Records));
        Assert.DoesNotContain(f.Records, r => r.IsRange);
    }

    [Fact]
    public async Task ScatteredInsertionsInAnOverCapArray_Resynchronise()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements * 2;
        var left = Enumerable.Range(0, count).ToList();
        var right = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (i % 1000 == 500)
                right.Add(-i);
            if (i % 1500 != 700)
                right.Add(i);
        }

        // Keep both ends different so nothing trims.
        right[0] = -1;
        right[^1] = -2;

        using var f = await DiffFixture.CreateAsync(Array(left), Array(right));

        Assert.True(f.Records[0].IsAlignmentApproximate);
        Assert.Equal(count / 1000, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(Enumerable.Range(0, count).Count(i => i % 1500 == 700), CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(3, CountStatus(f.Records, DiffStatus.Modified)); // the array and its two ends
    }

    [Fact]
    public async Task OverCapArraysThatDifferEverywhere_EndInOneRange()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements + JsonDiffIndex.MaxPositionalRecords;
        using var f = await DiffFixture.CreateAsync(
            Array(Enumerable.Range(0, count).Select(i => i * 2)),
            Array(Enumerable.Range(0, count).Select(i => i * 2 + 1)));

        Assert.True(f.Records[0].IsAlignmentApproximate);
        var range = Assert.Single(f.Records, r => r.IsRange);
        Assert.Equal(DiffStatus.Modified, range.Status);
        Assert.Equal(count - JsonDiffIndex.MaxPositionalRecords, range.LeftCount);
        Assert.Equal(count - JsonDiffIndex.MaxPositionalRecords, range.RightCount);
        Assert.Equal(JsonDiffIndex.MaxPositionalRecords, range.LeftOrdinal);
        Assert.Equal(range.Index + 1, f.Records.Count);
    }

    [Fact]
    public async Task RandomScalarArrays_EveryOrdinalCoveredExactlyOnce()
    {
        var random = new Random(0x5EED);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            int[] left = Enumerable.Range(0, random.Next(1, 80)).Select(_ => random.Next(20)).ToArray();
            int[] right = Enumerable.Range(0, random.Next(1, 80)).Select(_ => random.Next(20)).ToArray();

            using var f = await DiffFixture.CreateAsync(Array(left), Array(right));
            AssertCoverage(f.Records, left, right);
        }
    }

    /// <summary>Every element of each side is covered by exactly one record, and a record pairs
    /// equal elements exactly when it says they are unchanged or moved.</summary>
    private static void AssertCoverage(List<JsonDiffRecord> records, int[] left, int[] right)
    {
        if (records[0].IsRun)
        {
            Assert.Equal(left, right);
            return;
        }

        var children = records.Where(r => r.ParentRecord == 0).ToList();
        Assert.Equal(Enumerable.Range(0, left.Length),
            children.SelectMany(r => Ordinals(r.LeftOrdinal, r.LeftCount)).Order());
        Assert.Equal(Enumerable.Range(0, right.Length),
            children.SelectMany(r => Ordinals(r.RightOrdinal, r.RightCount)).Order());

        foreach (var record in children.Where(r => r.LeftCount > 0 && r.RightCount > 0 && !r.IsRange))
        {
            for (long k = 0; k < record.LeftCount; k++)
            {
                bool equal = left[record.LeftOrdinal + k] == right[record.RightOrdinal + k];
                Assert.Equal(equal, record.Status is DiffStatus.Unchanged or DiffStatus.Moved);
            }
        }

        static IEnumerable<int> Ordinals(long first, long count)
            => count <= 0 ? Enumerable.Empty<int>() : Enumerable.Range((int)first, (int)count);
    }

    [Fact]
    public async Task Anchors_NeverDecreaseAlongTheLog()
    {
        for (int seed = 0; seed < 10; seed++)
        {
            var random = new Random(seed);
            string leftJson = Encoding.UTF8.GetString(Support.RandomJson.Document(random, elements: 60));
            string rightJson = Encoding.UTF8.GetString(Support.RandomJson.Document(random, elements: 60));

            // The main descent; a similar move's children follow it, anchored where the move is.
            using var f = await DiffFixture.CreateAsync(leftJson, rightJson);
            for (int i = 1; i < f.Diff.MainRecordCount; i++)
                Assert.True(f.Records[i].LeftAnchor >= f.Records[i - 1].LeftAnchor, $"seed {seed}, record {i}");
        }
    }

    // ── Identity keys ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IdentityKey_ReorderAndEdit_IsAMovedModification()
    {
        using var f = await DiffFixture.CreateAsync(
            """[{"id":"a","v":1},{"id":"b","v":2},{"id":"c","v":3},{"id":"d","v":4}]""",
            """[{"id":"c","v":30},{"id":"a","v":1},{"id":"b","v":2},{"id":"d","v":4}]""");

        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));

        var moved = Assert.Single(f.Records, r => r.IsMovedWithin);
        Assert.Equal(DiffStatus.Modified, moved.Status);
        Assert.Equal(2, moved.LeftOrdinal);
        Assert.Equal(0, moved.RightOrdinal);
        Assert.True(moved.HasChildRecords);

        // Beneath it, only "v" changed.
        var leaf = Assert.Single(f.Records, r => r.ParentRecord == moved.Index && r.Status == DiffStatus.Modified);
        Assert.Equal(1, leaf.LeftOrdinal);
    }

    [Fact]
    public async Task IdentityKey_ReplacedElement_IsRemovedAndAdded_NotModified()
    {
        using var f = await DiffFixture.CreateAsync(
            """[{"userId":1,"n":"x"},{"userId":2,"n":"y"},{"userId":3,"n":"z"},{"userId":4,"n":"w"}]""",
            """[{"userId":1,"n":"x"},{"userId":9,"n":"q"},{"userId":3,"n":"Z"},{"userId":4,"n":"w"}]""");

        // By position this would be two modified elements; the keys say user 2 went, user 9 came,
        // and only user 3 changed.
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Added));
        var changed = Assert.Single(f.Records, r => r.Status == DiffStatus.Modified && r.ParentRecord == 0);
        Assert.Equal(2, changed.LeftOrdinal);
        Assert.False(changed.IsMovedWithin);
    }

    [Fact]
    public async Task IdentityKey_DuplicateValues_FallBackToAnchors()
    {
        using var f = await DiffFixture.CreateAsync(
            """[{"id":1,"v":1},{"id":1,"v":2},{"id":3,"v":3}]""",
            """[{"id":3,"v":3},{"id":1,"v":1},{"id":1,"v":9}]""");

        Assert.DoesNotContain(f.Records, r => r.IsMovedWithin);
    }

    [Theory]
    [InlineData("id", true)]
    [InlineData("ID", true)]
    [InlineData("_id", true)]
    [InlineData("uuid", true)]
    [InlineData("GUID", true)]
    [InlineData("key", true)]
    [InlineData("userId", true)]
    [InlineData("order_id", true)]
    [InlineData("SKU-id", true)]
    [InlineData("paid", false)]
    [InlineData("valid", false)]
    [InlineData("name", false)]
    [InlineData("Id", true)]
    public void IdentityNames(string name, bool expected)
        => Assert.Equal(expected, JsonDiffIndex.IsIdentityName(Encoding.UTF8.GetBytes(name)));

    // ── Cross-parent move reconciliation ───────────────────────────────────────────────

    [Fact]
    public async Task RelocatedSubtree_OneMovedPair_NoAddedRemoved()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"config":{"db":{"host":"x","port":5432}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"x","port":5432}}}""");

        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));

        var moved = f.Records.Where(r => r.Status == DiffStatus.Moved).ToList();
        Assert.Equal(2, moved.Count);

        var source = Assert.Single(moved, r => r.IsMoveSource);
        var target = Assert.Single(moved, r => !r.IsMoveSource);
        Assert.Equal(target.Index, source.MovePartnerRecord);
        Assert.Equal(source.Index, target.MovePartnerRecord);
        Assert.Equal(source.Left, target.Left);
        Assert.Equal(source.Right, target.Right);
        Assert.True(source.Left.IsPresent && source.Right.IsPresent);
        Assert.Equal(source.LeftEnd, target.LeftEnd);
        Assert.Equal(source.RightOrdinal, target.RightOrdinal);
    }

    [Fact]
    public async Task RelocatedOneOfTwoIdenticalSubtrees_StaysAddedRemoved()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":{"k":1},"b":{"k":1},"keep":0}""",
            """{"keep":0,"moved":{"k":1}}""");

        // Two identical subtrees were removed but only one reappeared - which one moved is
        // ambiguous (the removed-record bucket holds the hash twice), so no pairing.
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Moved));
        Assert.True(CountStatus(f.Records, DiffStatus.Removed) >= 1);
        Assert.True(CountStatus(f.Records, DiffStatus.Added) >= 1);
    }

    [Fact]
    public async Task RenamedKeyOverUnchangedContainer_OneMovedPair()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"old":{"x":1,"y":2}}""",
            """{"new":{"x":1,"y":2}}""");

        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Moved));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));
    }

    // ── Similarity pairing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RelocatedAndEdited_IsAMoveWithTheEditInside()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"config":{"db":{"host":"x","port":5432,"user":"u"}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"x","port":9999,"user":"u"}}}""");

        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));

        var source = Assert.Single(f.Records, r => r.Status == DiffStatus.Moved && r.IsMoveSource);
        var destination = Assert.Single(f.Records, r => r.Status == DiffStatus.Moved && !r.IsMoveSource);
        Assert.Equal(destination.Index, source.MovePartnerRecord);
        Assert.False(source.HasChildRecords);

        // The destination's children follow the descent, and only "port" changed among them.
        Assert.True(destination.HasChildRecords);
        Assert.True(destination.FirstChild >= f.Diff.MainRecordCount);
        var children = f.Records.Where(r => r.ParentRecord == destination.Index).ToList();
        Assert.All(children, c => Assert.InRange(c.Index, destination.FirstChild, destination.ChildrenEnd - 1));
        var port = Assert.Single(children, c => c.Status == DiffStatus.Modified);
        Assert.Equal(1, port.LeftOrdinal);
        Assert.Equal(2, children.Where(c => c.IsRun).Sum(c => c.LeftCount));

        Assert.Equal(2, source.LeftDepth);
        Assert.Equal(2, destination.RightDepth);
        Assert.Equal(3, port.LeftDepth);
        Assert.Equal(3, port.RightDepth);
    }

    [Fact]
    public async Task SimilarityPairsAcrossDepths()
    {
        // An added container is emitted whole, so the destination has to be a member added to a
        // container both documents have - here "x", one level deeper than where it came from.
        using var f = await DiffFixture.CreateAsync(
            """{"a":{"block":{"k1":1,"k2":2,"k3":3,"k4":4}},"b":{"x":{}}}""",
            """{"a":{},"b":{"x":{"block":{"k1":1,"k2":2,"k3":3,"k4":5}}}}""");

        var destination = Assert.Single(f.Records, r => r.Status == DiffStatus.Moved && !r.IsMoveSource);
        Assert.Equal(2, destination.LeftDepth);
        Assert.Equal(3, destination.RightDepth);
        Assert.Equal(3, destination.Depth);
        var k4 = Assert.Single(f.Records, r => r.ParentRecord == destination.Index && r.Status == DiffStatus.Modified);
        Assert.Equal(3, k4.LeftDepth);
        Assert.Equal(4, k4.RightDepth);
        Assert.Equal(4, k4.Depth);
    }

    [Fact]
    public async Task TooDissimilar_StaysAddedRemoved()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"config":{"db":{"host":"x","port":5432}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"y","port":9999}}}""");

        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Moved));
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Added));
    }

    [Fact]
    public async Task TheMostSimilarCandidateWins()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"old":{"a":1,"b":2,"c":3,"d":4},"keep":0}""",
            """{"keep":0,"close":{"a":1,"b":2,"c":3,"d":9},"far":{"a":1,"b":2,"x":7,"y":8}}""");

        var destination = Assert.Single(f.Records, r => r.Status == DiffStatus.Moved && !r.IsMoveSource);
        Assert.Contains("close", Encoding.UTF8.GetString(File.ReadAllBytes(f.RightPath))[(int)destination.Right.RowStart..(int)destination.Right.ValueStart]);
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Added)); // "far"
    }

    [Fact]
    public async Task PastThePairCap_TheSimilarityPassIsSkipped()
    {
        int side = (int)Math.Sqrt(JsonDiffIndex.MaxSimilarityPairs) + 2;
        string Blocks(string prefix, int changed) => "{" + string.Join(',', Enumerable.Range(0, side)
            .Select(i => $"\"{prefix}{i}\":{{\"a\":{i},\"b\":{i + 1},\"c\":{(i == changed ? -1 : i + 2)}}}")) + "}";

        using var f = await DiffFixture.CreateAsync(
            "{\"from\":" + Blocks("x", -1) + ",\"to\":{}}",
            "{\"from\":{},\"to\":" + Blocks("y", 3) + "}");

        // Every block but one moved unchanged, so exact pairing takes those. Only one edited block
        // is left on each side - well under the cap - so it is paired.
        Assert.Single(f.Records, r => r.Status == DiffStatus.Moved && !r.IsMoveSource && r.HasChildRecords);

        using var many = await DiffFixture.CreateAsync(
            "{\"from\":" + Blocks("x", -1) + ",\"to\":{}}",
            "{\"from\":{},\"to\":" + Blocks("y", -1).Replace("\"a\":", "\"z\":") + "}");

        // Every block edited: past the cap, nothing is scored.
        Assert.DoesNotContain(many.Records, r => r.Status == DiffStatus.Moved);
    }

    // ── Record-log structure ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DescendedContainer_SubtreeEndCoversChildren()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":{"x":1},"b":2}""",
            """{"a":{"x":9},"b":2}""");

        var root = f.Records[0];
        Assert.Equal(f.Records.Count, root.SubtreeEnd);

        var nested = f.Records.Single(r => r.ParentRecord == 0 && r.Status == DiffStatus.Modified);
        Assert.True(nested.SubtreeEnd > nested.Index + 1);
        Assert.All(f.Records.Where(r => r.ParentRecord == nested.Index),
            r => Assert.True(r.Index > nested.Index && r.Index < nested.SubtreeEnd));
    }

    [Fact]
    public async Task RemovedSubtree_SingleRecordNoDescent()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"keep":1,"gone":{"deep":{"deeper":[1,2,3]}}}""",
            """{"keep":1}""");

        var removed = Assert.Single(f.Records, r => r.Status == DiffStatus.Removed);
        Assert.Equal(removed.Index + 1, removed.SubtreeEnd);
        Assert.False(removed.Right.IsPresent);
        Assert.Equal(0, removed.RightCount);
    }

    [Fact]
    public async Task RecordsCarryTheirEnds()
    {
        string leftJson = """{"a":[1,2,3],"b":"x"}""";
        using var f = await DiffFixture.CreateAsync(leftJson, """{"a":[1,2,4],"b":"x"}""");

        var bRun = f.Records.Last(r => r.IsRun && r.ParentRecord == 0);
        Assert.Equal("\"x\"", leftJson[(int)bRun.Left.ValueStart..(int)bRun.LeftEnd]);
    }
}
