using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The diff row collection's walk: merged rows carry the correct sides, changed subtrees
/// auto-expand, whole-subtree regions expand into single-side token rows, the changes-only
/// filter drops Unchanged rows, and - mirroring CollectionDisposedEmptyTests - a disposed
/// collection reports empty so the trailing ItemsSource walk during a content swap reads
/// nothing.
/// </summary>
public class JsonDiffRowCollectionTests
{
    private sealed class Fixture : IDisposable
    {
        public JsonDiffSession Session { get; private init; } = null!;
        public JsonDiffRowCollection Rows { get; private init; } = null!;
        private readonly List<string> paths = new();

        public static async Task<Fixture> CreateAsync(string leftJson, string rightJson)
        {
            string leftPath = WriteTemp(leftJson);
            string rightPath = WriteTemp(rightJson);

            var session = JsonDiffSession.Start(leftPath, rightPath);
            try { await session.Diff.IndexingTask; } catch { }

            // Diff complete before the collection is built, so no growth monitor (and no
            // dispatcher) is involved - the same technique the JSON view's tests use.
            var rows = new JsonDiffRowCollection(session);
            return new Fixture { Session = session, Rows = rows, paths = { leftPath, rightPath } };
        }

        private static string WriteTemp(string json)
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        public List<JsonDiffRow> Materialize()
        {
            var list = new List<JsonDiffRow>();
            for (int i = 0; i < Rows.Count; i++)
                list.Add((JsonDiffRow)Rows[i]!);
            return list;
        }

        public void Dispose()
        {
            Rows.Dispose();
            Session.Dispose();
            foreach (var p in paths)
                File.Delete(p);
        }
    }

    [Fact]
    public async Task IdenticalDocuments_OneCollapsedUnchangedRow()
    {
        using var f = await Fixture.CreateAsync("""{"a":1,"b":2}""", """{"a":1,"b":2}""");

        var rows = f.Materialize();
        var row = Assert.Single(rows);
        Assert.Equal(DiffStatus.Unchanged, row.Status);
        Assert.False(row.IsExpanded);
        Assert.NotNull(row.Left);
        Assert.NotNull(row.Right);
    }

    [Fact]
    public async Task ModifiedLeaf_AutoExpandedDownToTheChange()
    {
        using var f = await Fixture.CreateAsync(
            """{"outer":{"inner":{"v":1,"w":2}}}""",
            """{"outer":{"inner":{"v":9,"w":2}}}""");

        var rows = f.Materialize();

        // Every container on the changed path is auto-expanded, so the modified leaf is
        // visible without any clicks; the unchanged sibling shows too.
        Assert.Contains(rows, r => r.Status == DiffStatus.Modified && r.Left is { Name: "v", Value: "1" } && r.Right is { Value: "9" });
        Assert.Contains(rows, r => r.Status == DiffStatus.Unchanged && r.Left is { Name: "w" });
    }

    [Fact]
    public async Task ChangesOnly_DropsUnchangedRows()
    {
        using var f = await Fixture.CreateAsync(
            """{"changed":1,"same":2}""",
            """{"changed":9,"same":2}""");

        f.Rows.ChangesOnly = true;
        var rows = f.Materialize();

        Assert.DoesNotContain(rows, r => r.Status == DiffStatus.Unchanged);
        Assert.Contains(rows, r => r.Status == DiffStatus.Modified && r.Left is { Name: "changed" });
    }

    [Fact]
    public async Task AddedAndRemovedRows_CarryOnlyTheirSide()
    {
        using var f = await Fixture.CreateAsync(
            """{"gone":1,"same":0}""",
            """{"same":0,"new":2}""");

        var rows = f.Materialize();

        var removed = Assert.Single(rows, r => r.Status == DiffStatus.Removed);
        Assert.NotNull(removed.Left);
        Assert.Null(removed.Right);

        var added = Assert.Single(rows, r => r.Status == DiffStatus.Added);
        Assert.Null(added.Left);
        Assert.NotNull(added.Right);
    }

    [Fact]
    public async Task ExpandingRemovedContainer_ShowsLeftOnlySubRows()
    {
        using var f = await Fixture.CreateAsync(
            """{"gone":{"x":1,"y":2},"same":0}""",
            """{"same":0}""");

        var rows = f.Materialize();
        var removed = Assert.Single(rows, r => r.Status == DiffStatus.Removed);
        Assert.True(removed.HasChildren);
        Assert.False(removed.IsExpanded);

        f.Rows.ToggleExpand(removed.Position);
        rows = f.Materialize();

        var subRows = rows.Where(r => r.Status == DiffStatus.Removed && r.Left is { Name: "x" or "y" }).ToList();
        Assert.Equal(2, subRows.Count);
        Assert.All(subRows, r => Assert.Null(r.Right));
    }

    [Fact]
    public async Task ExpandingUnchangedContainer_MirrorsLeftContentIntoBothPanes()
    {
        using var f = await Fixture.CreateAsync(
            """{"same":{"x":1},"changed":0}""",
            """{"same":{"x":1},"changed":9}""");

        var rows = f.Materialize();
        var unchanged = Assert.Single(rows, r => r.Status == DiffStatus.Unchanged && r.HasChildren);

        f.Rows.ToggleExpand(unchanged.Position);
        rows = f.Materialize();

        var sub = Assert.Single(rows, r => r.Left is { Name: "x" });
        Assert.NotNull(sub.Right);
        Assert.Same(sub.Left, sub.Right); // mirrored - identical content by definition
    }

    [Fact]
    public async Task CrossParentMove_RendersStubAndDestinationWithBadges()
    {
        using var f = await Fixture.CreateAsync(
            """{"config":{"db":{"host":"x"}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"x"}}}""");

        var rows = f.Materialize();
        var moved = rows.Where(r => r.Status == DiffStatus.Moved).ToList();
        Assert.Equal(2, moved.Count);

        var stub = Assert.Single(moved, r => r.Left is not null);
        var destination = Assert.Single(moved, r => r.Right is not null);
        Assert.Contains("moved to", stub.MoveBadge);
        Assert.Contains("meta", stub.MoveBadge);
        Assert.Contains("moved from", destination.MoveBadge);
        Assert.Contains("config", destination.MoveBadge);
    }

    [Fact]
    public async Task InArrayMove_BadgedWithSourceIndex()
    {
        using var f = await Fixture.CreateAsync("[1,2,3,4]", "[3,2,1,4]");

        var rows = f.Materialize();
        var moved = rows.Where(r => r.Status == DiffStatus.Moved).ToList();
        Assert.Equal(2, moved.Count);
        Assert.All(moved, r => Assert.Contains("moved from [", r.MoveBadge));
    }

    [Fact]
    public async Task Disposed_ReportsEmpty()
    {
        var f = await Fixture.CreateAsync("""{"a":1}""", """{"a":2}""");
        Assert.True(f.Rows.Count > 0);

        f.Rows.Dispose();

        // Mirrors CollectionDisposedEmptyTests: the trailing ItemsSource walk during a
        // content swap must read nothing from a disposed, mapping-free collection.
        int countAfterDispose = f.Rows.Count;
        Assert.Equal(0, countAfterDispose);
        Assert.Null(f.Rows[0]);

        f.Session.Dispose();
    }
}
