using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The headless differ (diff plan stages 2-3): Merkle short-circuit, name-based object
/// matching, merged emission order, histogram array alignment with in-array moves, the
/// over-cap approximate fallback, and cross-parent move reconciliation including the
/// documented v1 hole (moved-and-edited stays Added/Removed).
/// </summary>
public class JsonDiffIndexTests
{
    private sealed class DiffFixture : IDisposable
    {
        public JsonDiffIndex Diff { get; private set; } = null!;
        public List<JsonDiffRecord> Records { get; private set; } = null!;
        private readonly List<MMapFile> owned = new();
        private readonly List<string> paths = new();

        public static async Task<DiffFixture> CreateAsync(string leftJson, string rightJson)
        {
            var fixture = new DiffFixture();

            var (leftIndex, leftFile) = await fixture.IndexAsync(leftJson);
            var (rightIndex, rightFile) = await fixture.IndexAsync(rightJson);

            fixture.Diff = JsonDiffIndex.Start(leftIndex, leftFile, rightIndex, rightFile);
            await fixture.Diff.IndexingTask;

            fixture.Records = new List<JsonDiffRecord>();
            for (int i = 0; i < fixture.Diff.RecordCount; i++)
                fixture.Records.Add(fixture.Diff.GetRecord(i));

            return fixture;
        }

        private async Task<(JsonStructureIndex, MMapFile)> IndexAsync(string json)
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path, json, new UTF8Encoding(false));
            var file = new MMapFile(path);
            owned.Add(file);
            paths.Add(path);
            var index = JsonStructureIndex.StartIndexing(file, new JsonIndexOptions { ComputeContentHashes = true });
            await index.IndexingTask;
            return (index, file);
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

    // ── Merkle short-circuit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task IdenticalDocuments_SingleUnchangedRecord()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":[1,2,{"c":true}]}""",
            """{"a":1,"b":[1,2,{"c":true}]}""");

        var record = Assert.Single(f.Records);
        Assert.Equal(DiffStatus.Unchanged, record.Status);
        Assert.Equal(0, record.LeftToken);
        Assert.Equal(0, record.RightToken);
    }

    [Fact]
    public async Task KeyOrderOnlyDifference_SingleUnchangedRecord()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":{"x":1,"y":2}}""",
            """{"b":{"y":2,"x":1},"a":1}""");

        Assert.Single(f.Records);
        Assert.Equal(DiffStatus.Unchanged, f.Records[0].Status);
    }

    // ── Object levels ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScalarEdit_ModifiedLeafWithUnchangedSiblings()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"a":1,"b":2,"c":3}""",
            """{"a":1,"b":99,"c":3}""");

        // Root Modified (descended) + three member records.
        Assert.Equal(4, f.Records.Count);
        Assert.Equal(DiffStatus.Modified, f.Records[0].Status);
        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Unchanged));
        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Modified));

        var modifiedLeaf = f.Records.Single(r => r.Status == DiffStatus.Modified && r.Index > 0);
        Assert.Equal(1, modifiedLeaf.Depth);
        Assert.Equal(0, modifiedLeaf.ParentRecord);
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

    // ── Arrays (stage 3) ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAtHeadOfLargeArray_OneAddedZeroModified()
    {
        string left = "[" + string.Join(',', Enumerable.Range(0, 1000)) + "]";
        string right = "[-5," + string.Join(',', Enumerable.Range(0, 1000)) + "]";
        using var f = await DiffFixture.CreateAsync(left, right);

        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Added));
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Removed));
        // Only the container itself is Modified; every element pair is Unchanged.
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Modified));
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
        Assert.All(moved, r => Assert.True(r.LeftArrayIndex >= 0 && r.RightArrayIndex >= 0));
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

        // The changed element recursed: its "v" member is a Modified leaf, its "id" Unchanged.
        var leaf = f.Records.Single(r => r.Status == DiffStatus.Modified && r.SubtreeEnd == r.Index + 1);
        Assert.Equal(2, leaf.Depth);
    }

    [Fact]
    public async Task AllIdenticalElements_NoAnchors_Terminates()
    {
        string left = "[" + string.Join(',', Enumerable.Repeat("0", 500)) + "]";
        string right = "[" + string.Join(',', Enumerable.Repeat("0", 499)) + "]";
        using var f = await DiffFixture.CreateAsync(left, right);

        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(499, CountStatus(f.Records, DiffStatus.Unchanged));
    }

    [Fact]
    public async Task ArrayBeyondCap_FlagsApproximateAndDoesNotDescend()
    {
        string left = "[" + string.Join(',', Enumerable.Range(0, JsonDiffIndex.MaxAlignableArrayElements + 10)) + "]";
        string right = "[" + string.Join(',', Enumerable.Range(1, JsonDiffIndex.MaxAlignableArrayElements + 10)) + "]";
        using var f = await DiffFixture.CreateAsync(left, right);

        var root = Assert.Single(f.Records);
        Assert.Equal(DiffStatus.Modified, root.Status);
        Assert.True(root.IsAlignmentApproximate);
    }

    [Fact]
    public async Task ArrayAtExactCap_IsFullyAligned()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements;
        string prefix = string.Join(',', Enumerable.Range(0, count - 1));
        string left = $"[{prefix},{count - 1}]";
        string right = $"[{prefix},-1]";
        using var f = await DiffFixture.CreateAsync(left, right);

        Assert.False(f.Records[0].IsAlignmentApproximate);
        Assert.Equal(count + 1, f.Records.Count); // root plus every aligned element
        Assert.Equal(count - 1, CountStatus(f.Records, DiffStatus.Unchanged));
        Assert.Equal(2, CountStatus(f.Records, DiffStatus.Modified)); // root plus changed tail
    }

    [Fact]
    public async Task RandomScalarArrays_AlignmentConsumesEveryOrdinalExactlyOnce()
    {
        var random = new Random(0x5EED);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            int[] left = Enumerable.Range(0, random.Next(1, 80))
                .Select(_ => random.Next(20)).ToArray();
            int[] right = Enumerable.Range(0, random.Next(1, 80))
                .Select(_ => random.Next(20)).ToArray();

            using var f = await DiffFixture.CreateAsync(
                $"[{string.Join(',', left)}]",
                $"[{string.Join(',', right)}]");

            var children = f.Records.Where(r => r.ParentRecord == 0).ToList();
            Assert.Equal(Enumerable.Range(0, left.Length),
                children.Where(r => r.LeftArrayIndex >= 0).Select(r => r.LeftArrayIndex).Order());
            Assert.Equal(Enumerable.Range(0, right.Length),
                children.Where(r => r.RightArrayIndex >= 0).Select(r => r.RightArrayIndex).Order());

            foreach (var record in children.Where(r => r.LeftArrayIndex >= 0 && r.RightArrayIndex >= 0))
            {
                bool equal = left[record.LeftArrayIndex] == right[record.RightArrayIndex];
                Assert.Equal(equal, record.Status is DiffStatus.Unchanged or DiffStatus.Moved);
            }
        }
    }

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
        Assert.Equal(source.LeftToken, target.LeftToken);
        Assert.Equal(source.RightToken, target.RightToken);
        Assert.True(source.LeftToken >= 0 && source.RightToken >= 0);
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

    [Fact]
    public async Task RelocatedAndEdited_StaysAddedRemoved_TheDocumentedV1Hole()
    {
        using var f = await DiffFixture.CreateAsync(
            """{"config":{"db":{"host":"x","port":5432}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"x","port":9999}}}""");

        // The hash genuinely changed, so v1's exact-hash pairing cannot see the move.
        // The v2 similarity pass is designed to flip this test.
        Assert.Equal(0, CountStatus(f.Records, DiffStatus.Moved));
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Removed));
        Assert.Equal(1, CountStatus(f.Records, DiffStatus.Added));
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
        Assert.Equal(-1, removed.RightToken);
    }
}
