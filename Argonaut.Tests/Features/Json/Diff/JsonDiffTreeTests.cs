using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Diff;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Json.Diff;

/// <summary>
/// The merged diff tree the surface draws: which rows the log gives, what each pane of them says,
/// how they open and close, and that the cursor's forward walk, backward walk and seeks agree.
/// </summary>
public class JsonDiffTreeTests
{
    internal sealed class Fixture : IDisposable
    {
        public JsonDiffSession Session { get; private init; } = null!;
        public JsonDiffTree Tree { get; private init; } = null!;
        private readonly List<string> paths = new();

        public static async Task<Fixture> CreateAsync(string leftJson, string rightJson)
        {
            string leftPath = WriteTemp(leftJson);
            string rightPath = WriteTemp(rightJson);

            var session = LoadFromPath.StartDiff(leftPath, rightPath);
            try { await session.Diff.IndexingTask; } catch { }

            return new Fixture { Session = session, Tree = new JsonDiffTree(session), paths = { leftPath, rightPath } };
        }

        private static string WriteTemp(string json)
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        /// <summary>Every row as shown, top to bottom.</summary>
        public List<TreeRow> Rows()
        {
            var rows = new List<TreeRow>();
            var cursor = Tree.NewCursor();
            if (!cursor.MoveToStart())
                return rows;

            do
                rows.Add(cursor.Current);
            while (cursor.MoveNext());

            return rows;
        }

        /// <summary>Every row, bottom to top.</summary>
        public List<TreeRow> RowsBackward()
        {
            var rows = new List<TreeRow>();
            var cursor = Tree.NewCursor();
            if (!cursor.MoveToEnd())
                return rows;

            do
                rows.Add(cursor.Current);
            while (cursor.MovePrevious());

            rows.Reverse();
            return rows;
        }

        public string Pane(in TreeRow row, int pane)
        {
            var runs = new List<TreeRun>();
            Tree.Painter.AppendPaneRuns(row, pane, runs);
            return string.Concat(runs.Select(r => r.Text));
        }

        public string Describe(in TreeRow row) => $"{new string(' ', row.Depth * 2)}{Pane(row, 0)} | {Pane(row, 1)}";

        public List<string> Lines() => Rows().Select(r => Describe(r)).ToList();

        public void ExpandAll()
        {
            for (int pass = 0; pass < 50; pass++)
            {
                var closed = Rows().FirstOrDefault(r => r.Shape == TreeRowShape.Open && !r.IsExpanded);
                if (closed.Detail is null)
                    return;
                Tree.SetExpanded(closed, true);
            }
        }

        public void Dispose()
        {
            Session.Dispose();
            foreach (var p in paths)
                File.Delete(p);
        }
    }

    private static JsonDiffRowDetail Detail(in TreeRow row) => (JsonDiffRowDetail)row.Detail!;

    [Fact]
    public async Task IdenticalDocuments_OneCollapsedRow()
    {
        using var f = await Fixture.CreateAsync("""{"a":1,"b":2}""", """{"a":1,"b":2}""");

        var row = Assert.Single(f.Rows());
        Assert.Equal(TreeRowShape.Open, row.Shape);
        Assert.False(row.IsExpanded);
        Assert.True(Detail(row).RightMirrorsLeft);
        Assert.Equal(f.Pane(row, 0), f.Pane(row, 1));
    }

    [Fact]
    public async Task ModifiedLeaf_OpenDownToTheChange()
    {
        using var f = await Fixture.CreateAsync(
            """{"a":1,"outer":{"v":1,"w":2}}""",
            """{"a":1,"outer":{"v":9,"w":2}}""");

        Assert.Equal(new[]
        {
            "● { | ● {",
            "  a: 1 | a: 1",
            "  ● outer: { | ● outer: {",
            "    v: 1 | v: 9",
            "    w: 2 | w: 2",
        }, f.Lines());

        var leaf = f.Rows()[3];
        Assert.True(Detail(leaf).IsValueChanged);
        Assert.Equal(TreeRowTint.Changed, f.Tree.Painter.PaneTint(leaf, 0));
    }

    [Fact]
    public async Task ChangesOnly_DropsRunRows()
    {
        using var f = await Fixture.CreateAsync(
            """{"a":1,"b":2,"c":3}""",
            """{"a":1,"b":9,"c":3}""");

        f.Tree.ChangesOnly = true;
        Assert.Equal(new[] { "● { | ● {", "  b: 2 | b: 9" }, f.Lines());
        Assert.Equal(f.Lines(), f.RowsBackward().Select(r => f.Describe(r)).ToList());
    }

    [Fact]
    public async Task AddedAndRemovedRows_CarryOnlyTheirSide()
    {
        using var f = await Fixture.CreateAsync(
            """{"keep":1,"gone":2}""",
            """{"keep":1,"new":3}""");

        var rows = f.Rows();
        var gone = Assert.Single(rows, r => f.Pane(r, 0).StartsWith("gone"));
        Assert.Equal("", f.Pane(gone, 1));
        Assert.Equal(TreeRowTint.Removed, f.Tree.Painter.PaneTint(gone, 0));

        var added = Assert.Single(rows, r => f.Pane(r, 1).StartsWith("new"));
        Assert.Equal("", f.Pane(added, 0));
        Assert.Equal(TreeRowTint.Added, f.Tree.Painter.PaneTint(added, 1));
    }

    [Fact]
    public async Task ExpandingARemovedContainer_ShowsItsLeftChildren()
    {
        using var f = await Fixture.CreateAsync(
            """{"keep":1,"gone":{"x":1,"y":[1,2]}}""",
            """{"keep":1}""");

        var gone = Assert.Single(f.Rows(), r => f.Pane(r, 0).StartsWith("gone"));
        Assert.False(gone.IsExpanded);
        f.Tree.Toggle(gone);

        var lines = f.Lines();
        Assert.Contains("    x: 1 | ", lines);
        Assert.Contains("    y: [ 2 items ] | ", lines);
        Assert.Equal(TreeRowTint.Removed, f.Tree.Painter.PaneTint(f.Rows().Single(r => f.Pane(r, 0) == "x: 1"), 0));
    }

    [Fact]
    public async Task ExpandingAnUnchangedMember_MirrorsItIntoBothPanes()
    {
        using var f = await Fixture.CreateAsync(
            """{"same":{"x":1},"v":1}""",
            """{"same":{"x":1},"v":2}""");

        var same = Assert.Single(f.Rows(), r => f.Pane(r, 0).StartsWith("same"));
        f.Tree.Toggle(same);

        var x = Assert.Single(f.Rows(), r => f.Pane(r, 0) == "x: 1");
        Assert.Equal("x: 1", f.Pane(x, 1));
        Assert.True(Detail(x).RightMirrorsLeft);
    }

    [Fact]
    public async Task CrossParentMove_StubAndDestinationSayWhereTheOtherIs()
    {
        using var f = await Fixture.CreateAsync(
            """{"config":{"db":{"host":"x"}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"x"}}}""");

        var lines = f.Lines();
        Assert.Contains(lines, l => l.Contains("db: { 1 member }   moved to $.meta.db → | "));
        Assert.Contains(lines, l => l.Contains(" | db: { 1 member }   ↕ moved from $.config.db"));
    }

    [Fact]
    public async Task MovedAndEdited_TheDestinationOpensOnTheEdit()
    {
        using var f = await Fixture.CreateAsync(
            """{"config":{"db":{"host":"x","port":5432,"user":"u"}},"meta":{"z":1}}""",
            """{"config":{},"meta":{"z":1,"db":{"host":"x","port":9999,"user":"u"}}}""");

        var lines = f.Lines();
        Assert.Contains(lines, l => l.Contains("moved to $.meta.db →"));
        Assert.Contains(lines, l => l.Contains("↕ moved from $.config.db, changed"));

        // The destination starts open, its children drawn from both sides: the edit between the
        // unchanged members, then the rows after the move carry on.
        int destination = lines.FindIndex(l => l.Contains("moved from $.config.db"));
        Assert.Equal("      host: \"x\" | host: \"x\"", lines[destination + 1]);
        Assert.Equal("      port: 5432 | port: 9999", lines[destination + 2]);
        Assert.Equal("      user: \"u\" | user: \"u\"", lines[destination + 3]);
        Assert.Equal(destination + 4, lines.Count);

        Assert.Equal(f.Rows().Select(r => r.Start), f.RowsBackward().Select(r => r.Start));
        var cursor = f.Tree.NewCursor();
        foreach (var row in f.Rows())
        {
            Assert.True(cursor.SeekTo(row.Start));
            Assert.Equal(row.Start, cursor.Current.Start);
        }
    }

    [Fact]
    public async Task MovedAndEdited_NextChangeVisitsTheEditWhereItIsShown()
    {
        using var f = await Fixture.CreateAsync(
            """{"config":{"db":{"host":"x","port":5432,"user":"u"}},"meta":{},"tail":1}""",
            """{"config":{},"meta":{"db":{"host":"x","port":9999,"user":"u"}},"tail":2}""");

        var visited = new List<string>();
        long? key = null;
        for (int i = 0; i < 4; i++)
        {
            key = f.Tree.NextChange(key, 1);
            visited.Add(f.Describe(f.Tree.Reveal(key!.Value)!.Value).Trim());
        }

        // The stub, the destination, the edit inside it, then the tail - merged order, though the
        // edit's record sits after all of these in the log.
        Assert.StartsWith("db:", visited[0]);
        Assert.Contains("moved from", visited[1]);
        Assert.StartsWith("port: 5432", visited[2]);
        Assert.StartsWith("tail: 1", visited[3]);
    }

    [Fact]
    public async Task MovedAndEdited_CollapsingTheDestinationHidesItsChildren()
    {
        using var f = await Fixture.CreateAsync(
            """{"a":{"block":{"k1":1,"k2":2,"k3":3}},"b":{}}""",
            """{"a":{},"b":{"block":{"k1":1,"k2":2,"k3":9}}}""");

        var destination = Assert.Single(f.Rows(), r => f.Pane(r, 1).Contains("moved from"));
        var k3 = Assert.Single(f.Rows(), r => f.Pane(r, 0) == "k3: 3");
        f.Tree.Toggle(destination);
        Assert.DoesNotContain(f.Rows(), r => f.Pane(r, 0) == "k3: 3");

        Assert.True(f.Tree.Hides(f.Rows().Single(r => r.Start == destination.Start), k3.Start));
        Assert.Equal(k3.Start, f.Tree.Reveal(k3.Start)?.Start);
    }

    [Fact]
    public async Task InArrayMove_BadgedWithItsSourceIndex()
    {
        using var f = await Fixture.CreateAsync("[1,2,3,4]", "[3,2,1,4]");

        Assert.Contains(f.Lines(), l => l.Contains("↕ moved from ["));
    }

    [Fact]
    public async Task ArrayElements_AreMarkedWithTheirIndexOnEachSide()
    {
        using var f = await Fixture.CreateAsync("[1,2,3]", "[0,1,2,3]");

        var one = Assert.Single(f.Rows(), r => f.Pane(r, 0) == "1");
        Assert.Equal("0", f.Tree.Painter.PaneMarker(one, 0));
        Assert.Equal("1", f.Tree.Painter.PaneMarker(one, 1));
    }

    [Fact]
    public async Task Range_ExpandsToTheLeftElementsThenTheRight()
    {
        int count = JsonDiffIndex.MaxAlignableArrayElements + JsonDiffIndex.MaxPositionalRecords + 2;
        using var f = await Fixture.CreateAsync(
            "[" + string.Join(',', Enumerable.Range(0, count).Select(i => i * 2)) + "]",
            "[" + string.Join(',', Enumerable.Range(0, count).Select(i => i * 2 + 1)) + "]");

        var cursor = f.Tree.NewCursor();
        Assert.True(cursor.MoveToEnd());
        var header = cursor.Current;
        Assert.StartsWith("… 100,002 more elements", f.Pane(header, 0));
        Assert.Contains("shown whole", f.Pane(header, 1));

        f.Tree.Toggle(header);
        cursor.SeekTo(header.Start);
        Assert.True(cursor.MoveNext());
        Assert.Equal($"{(count - 100_002) * 2}", f.Pane(cursor.Current, 0));
        Assert.Equal("", f.Pane(cursor.Current, 1));

        Assert.True(cursor.MoveToEnd());
        Assert.Equal("", f.Pane(cursor.Current, 0));
        Assert.Equal($"{(count - 1) * 2 + 1}", f.Pane(cursor.Current, 1));
    }

    [Fact]
    public async Task Reveal_OpensWhatHidesTheRow()
    {
        using var f = await Fixture.CreateAsync(
            """{"a":{"deep":{"x":1}},"b":1}""",
            """{"a":{"deep":{"x":2}},"b":1}""");

        var leaf = Assert.Single(f.Rows(), r => f.Pane(r, 0) == "x: 1");
        var outer = Assert.Single(f.Rows(), r => f.Pane(r, 0).Contains("a: {"));
        f.Tree.Toggle(outer);
        Assert.DoesNotContain(f.Rows(), r => f.Pane(r, 0) == "x: 1");

        var revealed = f.Tree.Reveal(leaf.Start);
        Assert.Equal(leaf.Start, revealed?.Start);
        Assert.Contains(f.Rows(), r => f.Pane(r, 0) == "x: 1");
    }

    [Fact]
    public async Task Reveal_OpensARegionRowInsideACollapsedNode()
    {
        using var f = await Fixture.CreateAsync(
            """{"gone":{"inner":{"x":1}}}""",
            """{}""");

        var gone = Assert.Single(f.Rows(), r => f.Pane(r, 0).StartsWith("gone"));
        f.Tree.Toggle(gone);
        var inner = Assert.Single(f.Rows(), r => f.Pane(r, 0).StartsWith("inner"));
        f.Tree.Toggle(inner);
        var x = Assert.Single(f.Rows(), r => f.Pane(r, 0) == "x: 1");
        f.Tree.Toggle(inner);
        f.Tree.Toggle(gone);

        Assert.Equal(x.Start, f.Tree.Reveal(x.Start)?.Start);
    }

    [Fact]
    public async Task NextChange_WalksTheChangesInOrderAndWraps()
    {
        using var f = await Fixture.CreateAsync(
            """{"a":1,"nested":{"changed":2},"gone":3}""",
            """{"a":1,"nested":{"changed":9}}""");

        long first = f.Tree.NextChange(null, 1)!.Value;
        var firstRow = f.Tree.Reveal(first)!.Value;
        Assert.Equal("changed: 2", f.Pane(firstRow, 0));

        long second = f.Tree.NextChange(first, 1)!.Value;
        Assert.StartsWith("gone", f.Pane(f.Tree.Reveal(second)!.Value, 0));

        Assert.Equal(first, f.Tree.NextChange(second, 1));
        Assert.Equal(second, f.Tree.NextChange(first, -1));
    }

    [Fact]
    public async Task IdenticalDocuments_HaveNoChangeToGoTo()
    {
        using var f = await Fixture.CreateAsync("""{"a":1}""", """{"a":1}""");

        Assert.Null(f.Tree.NextChange(null, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RandomDocuments_ForwardBackwardAndSeekAgree(int seed)
    {
        var random = new Random(seed);
        string leftJson = Encoding.UTF8.GetString(RandomJson.Document(random, elements: 12, maxDepth: 4));
        string rightJson = Mutate(leftJson, random);

        using var f = await Fixture.CreateAsync(leftJson, rightJson);
        f.ExpandAll();

        var forward = f.Rows();
        var backward = f.RowsBackward();
        Assert.Equal(forward.Select(r => r.Start), backward.Select(r => r.Start));

        // Keys follow the order rows are shown in, and every key seeks back to its row.
        for (int i = 1; i < forward.Count; i++)
            Assert.True(forward[i].Start > forward[i - 1].Start, $"row {i}");

        var cursor = f.Tree.NewCursor();
        foreach (var row in forward)
        {
            Assert.True(cursor.SeekTo(row.Start));
            Assert.Equal(row.Start, cursor.Current.Start);
            Assert.Equal(row.Depth, cursor.Current.Depth);
        }

        f.Tree.ChangesOnly = true;
        Assert.Equal(f.Rows().Select(r => r.Start), f.RowsBackward().Select(r => r.Start));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task RandomDocuments_AncestorsAreTheRowsAbove(int seed)
    {
        var random = new Random(seed);
        string leftJson = Encoding.UTF8.GetString(RandomJson.Document(random, elements: 8, maxDepth: 4));
        using var f = await Fixture.CreateAsync(leftJson, Mutate(leftJson, random));
        f.ExpandAll();

        var rows = f.Rows();
        var cursor = f.Tree.NewCursor();
        for (int i = 0; i < rows.Count; i++)
        {
            cursor.SeekTo(rows[i].Start);
            var ancestors = cursor.Ancestors.ToList();
            Assert.Equal(rows[i].Depth, ancestors.Count);

            // Each ancestor is the nearest row above at its depth.
            foreach (var ancestor in ancestors)
            {
                var above = rows.Take(i).Last(r => r.Depth == ancestor.Depth);
                Assert.Equal(above.Start, ancestor.Start);
            }
        }
    }

    [Fact]
    public async Task ScrollPositions_SeekBackToTheirRows()
    {
        var random = new Random(9);
        string leftJson = Encoding.UTF8.GetString(RandomJson.Document(random, elements: 20, maxDepth: 3));
        using var f = await Fixture.CreateAsync(leftJson, Mutate(leftJson, random));
        f.ExpandAll();

        var cursor = f.Tree.NewCursor();
        foreach (var row in f.Rows())
        {
            long position = f.Tree.ScrollPosition(row);
            f.Tree.SeekScrollPosition(cursor, position);
            Assert.True(position == f.Tree.ScrollPosition(cursor.Current),
                $"{f.Describe(row)} at {position} sought to {f.Describe(cursor.Current)} at {f.Tree.ScrollPosition(cursor.Current)}");
        }
    }

    [Fact]
    public async Task KeyForMatch_FindsTheRowDrawingAnOffset()
    {
        string left = """{"keep":{"x":"needle"},"gone":"needle"}""";
        string right = """{"keep":{"x":"needle"},"new":"needle"}""";
        using var f = await Fixture.CreateAsync(left, right);

        // The removed member: its own row.
        long gone = f.Tree.KeyForMatch(leftSide: true, left.LastIndexOf("needle", StringComparison.Ordinal))!.Value;
        Assert.StartsWith("gone", f.Pane(f.Tree.Reveal(gone)!.Value, 0));

        // Inside unchanged content: drawn from the left, so the left copy is a stop and the
        // right's is not.
        long kept = f.Tree.KeyForMatch(leftSide: true, left.IndexOf("needle", StringComparison.Ordinal))!.Value;
        Assert.Equal("x: \"needle\"", f.Pane(f.Tree.Reveal(kept)!.Value, 0));
        Assert.Null(f.Tree.KeyForMatch(leftSide: false, right.IndexOf("needle", StringComparison.Ordinal)));

        // The added member, from the right.
        long added = f.Tree.KeyForMatch(leftSide: false, right.LastIndexOf("needle", StringComparison.Ordinal))!.Value;
        Assert.StartsWith("new", f.Pane(f.Tree.Reveal(added)!.Value, 1));
    }

    /// <summary>A few scattered edits: some numbers changed, a string or two dropped.</summary>
    private static string Mutate(string json, Random random)
    {
        var text = new StringBuilder(json);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]) && random.Next(12) == 0)
                text[i] = (char)('1' + random.Next(9)); // never a leading zero
        }

        return text.ToString();
    }
}
