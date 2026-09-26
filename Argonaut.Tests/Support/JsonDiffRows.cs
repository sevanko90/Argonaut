using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Diff;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Support;

/// <summary>Reading a merged diff tree's rows the way a test wants them: all of them, and what
/// each side of one says.</summary>
internal static class JsonDiffRows
{
    /// <summary>Every row as shown, top to bottom.</summary>
    public static List<TreeRow> All(JsonDiffTree tree)
    {
        var rows = new List<TreeRow>();
        var cursor = tree.NewCursor();
        if (!cursor.MoveToStart())
            return rows;

        do
            rows.Add(cursor.Current);
        while (cursor.MoveNext());

        return rows;
    }

    public static JsonDiffRowDetail Detail(in TreeRow row) => (JsonDiffRowDetail)row.Detail!;

    /// <summary>One pane's text as painted.</summary>
    public static string Pane(JsonDiffTree tree, in TreeRow row, int pane)
    {
        var runs = new List<TreeRun>();
        tree.Painter.AppendPaneRuns(row, pane, runs);
        return string.Concat(runs.Select(r => r.Text));
    }

    /// <summary>The member name on whichever side has one, or null.</summary>
    public static string? Name(JsonDiffTree tree, in TreeRow row)
    {
        var detail = Detail(row);
        if (detail.Left is { } left)
            return tree.Left.Text.Name(left.Node);
        return detail.Right is { } right ? (detail.RightMirrorsLeft ? tree.Left : tree.Right).Text.Name(right.Node) : null;
    }

    /// <summary>One side's value as written - a scalar, or a container's summary - or null.</summary>
    public static string? Value(JsonDiffTree tree, in TreeRow row, bool leftSide)
    {
        var detail = Detail(row);
        var pane = leftSide ? detail.Left : detail.Right;
        if (pane is not { } drawn)
            return null;

        var document = leftSide || detail.RightMirrorsLeft ? tree.Left : tree.Right;
        return document.Text.ValueText(drawn.Node);
    }

    public static string Describe(JsonDiffTree tree)
        => string.Join("\n", All(tree).Select(r => $"  {Pane(tree, r, 0)} | {Pane(tree, r, 1)}"));
}
