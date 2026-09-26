using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Draws a merged diff row two panes wide: the left document's node on the left, the right
/// document's on the right, each as the JSON tree draws it (<see cref="JsonTreePainter"/>, without
/// hints or table links), washed by what the comparison found. A descended pair is marked as the
/// way to a change; a move says where it went or came from, beside the node at that end.
/// </summary>
public sealed class JsonDiffPainter : ITreeRowPainter
{
    private const string NoteGap = "   ";

    private readonly JsonDiffTree tree;
    private readonly JsonTreePainter left;
    private readonly JsonTreePainter right;

    public JsonDiffPainter(JsonDiffTree tree)
    {
        this.tree = tree;
        left = new JsonTreePainter(tree.Left.Text, hintProviders: null, offerArrayTable: false);
        right = new JsonTreePainter(tree.Right.Text, hintProviders: null, offerArrayTable: false);
    }

    public int PaneCount => 2;

    public void AppendRuns(in TreeRow row, List<TreeRun> runs) => AppendPaneRuns(row, 0, runs);

    public void AppendPaneRuns(in TreeRow row, int pane, List<TreeRun> runs)
    {
        if (row.Detail is not JsonDiffRowDetail detail)
            return;

        var record = tree.Diff.GetRecord(detail.Record);
        if (detail.IsRecordRow && record.IsRange)
        {
            AppendRangeHeader(record, pane, runs);
            return;
        }

        if ((pane == 0 ? detail.Left : detail.Right) is not { } side)
            return;

        if (detail.IsChangedPath)
            runs.Add(new TreeRun("● ", TreeRunStyle.Change));

        var shape = side.Node.IsContainer ? TreeRowShape.Open : TreeRowShape.Leaf;
        var sideRow = new TreeRow(shape, side.Node, side.Node.RowStart, row.Depth, side.Ordinal, side.ParentKind, -1,
            side.Node.IsContainer && row.IsExpanded);
        bool fromLeft = pane == 0 || detail.RightMirrorsLeft;
        (fromLeft ? left : right).AppendRuns(sideRow, runs);

        if (detail.IsRecordRow && Badge(record, pane) is { } badge)
            runs.Add(new TreeRun(NoteGap + badge, TreeRunStyle.Hint));
    }

    /// <summary>What a record's own row says beside its node at one end: where a move went or came
    /// from, or that an array was compared in place.</summary>
    private string? Badge(in JsonDiffRecord record, int pane)
    {
        if (record.IsCrossParentMove)
        {
            if (record.IsMoveSource && pane == 0)
                return $"moved to {tree.Right.Path(record.Right.ValueStart)} →";
            if (!record.IsMoveSource && pane == 1)
                return $"↕ moved from {tree.Left.Path(record.Left.ValueStart)}{(record.HasChildRecords ? ", changed" : "")}";
            return null;
        }

        if (pane == 1 && (record.Status == DiffStatus.Moved || record.IsMovedWithin))
            return $"↕ moved from [{record.LeftOrdinal}]";

        if (pane == 1 && record.IsAlignmentApproximate)
            return "compared in place — too long to align";

        return null;
    }

    private static void AppendRangeHeader(in JsonDiffRecord record, int pane, List<TreeRun> runs)
    {
        long count = pane == 0 ? record.LeftCount : record.RightCount;
        if (count == 0)
            return;

        runs.Add(new TreeRun($"… {count:N0} more {(count == 1 ? "element" : "elements")}", TreeRunStyle.Summary));
        if (pane == 1)
            runs.Add(new TreeRun(NoteGap + "too many differences to compare — shown whole", TreeRunStyle.Hint));
    }

    public string? PaneMarker(in TreeRow row, int pane)
    {
        if (row.Detail is not JsonDiffRowDetail detail || (pane == 0 ? detail.Left : detail.Right) is not { } side)
            return null;

        return side.ParentKind == (byte)JsonTokenKind.StartArray && side.Ordinal >= 0 ? side.Ordinal.ToString() : null;
    }

    public TreeRowTint PaneTint(in TreeRow row, int pane)
        => row.Detail is JsonDiffRowDetail detail ? pane == 0 ? detail.LeftTint : detail.RightTint : TreeRowTint.None;
}
