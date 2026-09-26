using Argonaut.Engine.Indexing.Trees;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Diff;

/// <summary>What one pane of a merged diff row draws: a node of that side's document, where it
/// sits among its siblings there, and what kind of container holds it.</summary>
public readonly record struct DiffPane(TreeNode Node, long Ordinal, byte ParentKind);

/// <summary>
/// What a merged diff row is, attached to its <see cref="TreeRow"/> by <see cref="JsonDiffCursor"/>
/// (the row's own node is only its key). The painter draws from it and the context bar reads the
/// selection from it.
/// </summary>
public sealed class JsonDiffRowDetail
{
    /// <summary>The record the row belongs to: its own row, or a row of one of its regions.</summary>
    public required int Record { get; init; }

    /// <summary>Which of the record's regions the row is in, or -1 for the record's own row.</summary>
    public required int Region { get; init; }

    /// <summary>What the left pane draws, or null for nothing.</summary>
    public DiffPane? Left { get; init; }

    /// <summary>What the right pane draws, or null for nothing.</summary>
    public DiffPane? Right { get; init; }

    /// <summary>The right pane shows the left document's node: unchanged content, equal on both
    /// sides, drawn from one. <see cref="Right"/> is then the left node at the right ordinal.</summary>
    public bool RightMirrorsLeft { get; init; }

    public TreeRowTint LeftTint { get; init; }

    public TreeRowTint RightTint { get; init; }

    /// <summary>A descended pair on the way to a change - not itself a difference.</summary>
    public bool IsChangedPath { get; init; }

    /// <summary>A leaf-level difference: what next/previous change stops on and the context bar
    /// splits character by character.</summary>
    public bool IsValueChanged { get; init; }

    /// <summary>For a region row, the document offset its key is measured from.</summary>
    public long RegionBase { get; init; }

    /// <summary>For a region row, whether the region walks the left document.</summary>
    public bool RegionIsLeft { get; init; }

    /// <summary>Where the row is on the scrollbar - a left-document offset (see
    /// <see cref="JsonDiffTree.ScrollPosition"/>).</summary>
    public long ScrollPosition { get; init; }

    /// <summary>A row inside a run of unchanged pairs: the run element holding it, on the left
    /// (its value start), and that element's ordinal on the right - what a right-side JSONPath is
    /// spliced from.</summary>
    public long MirrorLeftElement { get; init; } = -1;

    public long MirrorRightOrdinal { get; init; } = -1;

    /// <summary>The record's own row, expanded or collapsed by record rather than by node.</summary>
    public bool IsRecordRow => Region < 0;
}
