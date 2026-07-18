using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies <see cref="JsonVisibleRowCollection.EnsureVisible"/> - the mechanism behind
/// clicking a JSONPath breadcrumb segment - expands exactly the ancestors needed to reveal
/// a token, pages a large array's display limit up when the target is past it, and is a
/// true no-op (no rebuild/CollectionChanged) when the target is already visible.
/// </summary>
public class JsonVisibleRowCollectionTests
{
    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    private static int FindTokenIndex(JsonStructureIndex index, System.Func<JsonTokenInfo, bool> predicate)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            if (predicate(index.GetToken(i)))
                return i;
        }

        throw new System.InvalidOperationException("Token not found.");
    }

    [Fact]
    public void EnsureVisible_ExpandsCollapsedAncestor()
    {
        // Root auto-expands one level; "nested"'s own children start collapsed.
        const string json = "{\"nested\":{\"deep\":\"value\"}}";
        var (index, mmap, path) = BuildIndex(json);
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            int deepTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.String);

            Assert.Null(rows.FindVisiblePosition(deepTokenIndex));

            rows.EnsureVisible(deepTokenIndex);

            Assert.NotNull(rows.FindVisiblePosition(deepTokenIndex));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureVisible_NoOpWhenAlreadyVisible_DoesNotRebuild()
    {
        const string json = "{\"a\":1}";
        var (index, mmap, path) = BuildIndex(json);
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            int aTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number);

            // Root is auto-expanded at construction, so "a" is already visible.
            Assert.NotNull(rows.FindVisiblePosition(aTokenIndex));

            bool rebuilt = false;
            rows.CollectionChanged += (_, _) => rebuilt = true;

            rows.EnsureVisible(aTokenIndex);

            Assert.False(rebuilt, "EnsureVisible should not rebuild when the target is already visible.");
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureVisible_PagesArrayLimitPastDefaultChildCap()
    {
        // ChildCap defaults to 2000 direct children shown per expanded container; build an
        // array with an element well past that so EnsureVisible has to page the limit up
        // (not just expand the array) to make it reachable.
        var sb = new StringBuilder();
        sb.Append("{\"arr\":[");
        for (int i = 0; i < 2100; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(i);
        }

        sb.Append("]}");

        var (index, mmap, path) = BuildIndex(sb.ToString());
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);

            int arrTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.StartArray);
            var arrToken = index.GetToken(arrTokenIndex);

            int lastElementIndex = arrToken.EndIndex - 1; // last Number token before the closing bracket
            Assert.Equal(JsonTokenKind.Number, index.GetToken(lastElementIndex).Kind);

            Assert.Null(rows.FindVisiblePosition(lastElementIndex));

            rows.EnsureVisible(lastElementIndex);

            Assert.NotNull(rows.FindVisiblePosition(lastElementIndex));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureVisible_PagesObjectLimitPastDefaultChildCap()
    {
        // Same as the array case but for an object parent: a member past ChildCap (e.g. a
        // search hit deep in a wide object) must page the display limit up too.
        var sb = new StringBuilder();
        sb.Append("{\"obj\":{");
        for (int i = 0; i < 2100; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("\"k").Append(i).Append("\":").Append(i);
        }

        sb.Append("}}");

        var (index, mmap, path) = BuildIndex(sb.ToString());
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);

            int objTokenIndex = FindTokenIndex(index,
                t => t.Kind == JsonTokenKind.StartObject && t.ParentIndex == 0);
            var objToken = index.GetToken(objTokenIndex);

            int lastMemberIndex = objToken.EndIndex - 1; // last Number member before the closing brace
            Assert.Equal(JsonTokenKind.Number, index.GetToken(lastMemberIndex).Kind);

            Assert.Null(rows.FindVisiblePosition(lastMemberIndex));

            rows.EnsureVisible(lastMemberIndex);

            Assert.NotNull(rows.FindVisiblePosition(lastMemberIndex));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
