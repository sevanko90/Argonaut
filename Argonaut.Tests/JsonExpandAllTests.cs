using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the alt/option-click deep toggle (JsonVisibleRowCollection.ToggleExpandAll):
/// expand-all descendants of one node (never its siblings), collapse-all that forgets
/// descendant state, the row budget that bounds how much one deep-expand may reveal, and
/// that deep-expanded containers survive a default-expand-depth change as explicit overrides.
/// </summary>
public class JsonExpandAllTests
{
    // Root object containing a 3-deep chain under "a" and a sibling container "z":
    // {"a":{"b":{"c":[1,2]}},"z":{"y":9}}
    private const string NestedDoc = "{\"a\":{\"b\":{\"c\":[1,2]}},\"z\":{\"y\":9}}";

    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    /// <summary>Token index of the n-th (0-based, document order) token of the given kind.</summary>
    private static int NthOfKind(JsonStructureIndex index, JsonTokenKind kind, int n)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            if (index.GetToken(i).Kind == kind && n-- == 0)
                return i;
        }

        throw new InvalidOperationException("Token not found.");
    }

    private static int VisiblePosition(JsonVisibleRowCollection rows, int tokenIndex) =>
        rows.FindVisiblePosition(tokenIndex)
            ?? throw new InvalidOperationException("Token not visible.");

    [Fact]
    public void ExpandAll_ExpandsAllDescendants_ButNotSiblings()
    {
        var (index, mmap, path) = BuildIndex(NestedDoc);
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap, defaultExpandDepth: 1);
            int aIndex = NthOfKind(index, JsonTokenKind.StartObject, 1);

            bool budgetHit = rows.ToggleExpandAll(VisiblePosition(rows, aIndex));

            Assert.False(budgetHit);
            // Both array elements under a.b.c are now visible...
            Assert.NotNull(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.Number, 0)));
            Assert.NotNull(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.Number, 1)));
            // ...but the sibling container "z" stays collapsed (its member "y" hidden).
            Assert.Null(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.Number, 2)));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void CollapseAll_ForgetsDescendantState()
    {
        var (index, mmap, path) = BuildIndex(NestedDoc);
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap, defaultExpandDepth: 1);
            int aIndex = NthOfKind(index, JsonTokenKind.StartObject, 1);
            int bIndex = NthOfKind(index, JsonTokenKind.StartObject, 2);
            int cIndex = NthOfKind(index, JsonTokenKind.StartArray, 0);

            rows.ToggleExpandAll(VisiblePosition(rows, aIndex)); // deep expand
            rows.ToggleExpandAll(VisiblePosition(rows, aIndex)); // deep collapse
            Assert.Null(rows.FindVisiblePosition(bIndex));

            // Plain re-expand shows only direct children, in their default (collapsed) state.
            rows.ToggleExpand(VisiblePosition(rows, aIndex));
            Assert.NotNull(rows.FindVisiblePosition(bIndex));
            Assert.Null(rows.FindVisiblePosition(cIndex));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ExpandAll_StopsAtRowBudget()
    {
        // Five nested arrays: [[[[[9]]]]]
        var (index, mmap, path) = BuildIndex("[[[[[9]]]]]");
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap, defaultExpandDepth: 0);

            bool budgetHit = rows.ToggleExpandAll(0, rowBudget: 3);

            Assert.True(budgetHit);
            // Depth-3 array is visible (its parent got expanded before the budget ran out)
            // but stayed collapsed itself: the depth-4 array underneath is hidden.
            Assert.NotNull(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.StartArray, 3)));
            Assert.Null(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.StartArray, 4)));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ExpandAll_WithAmpleBudget_RevealsWholeSubtree()
    {
        var (index, mmap, path) = BuildIndex("[[[[[9]]]]]");
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap, defaultExpandDepth: 0);

            bool budgetHit = rows.ToggleExpandAll(0);

            Assert.False(budgetHit);
            Assert.NotNull(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.Number, 0)));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ExpandAll_SurvivesDefaultDepthChange()
    {
        var (index, mmap, path) = BuildIndex(NestedDoc);
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap, defaultExpandDepth: 1);
            int aIndex = NthOfKind(index, JsonTokenKind.StartObject, 1);

            rows.ToggleExpandAll(VisiblePosition(rows, aIndex));

            // Dropping the default collapses the root (it was only default-expanded), but
            // the deep-expanded chain under "a" became explicit overrides: re-expanding the
            // root shows it fully open again.
            rows.SetDefaultExpandDepth(0);
            Assert.Null(rows.FindVisiblePosition(aIndex));

            rows.ToggleExpand(0);
            Assert.NotNull(rows.FindVisiblePosition(NthOfKind(index, JsonTokenKind.Number, 0)));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
