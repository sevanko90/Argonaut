using System;
using Argonaut.Engine.Indexing.Trees;
using Avalonia;

namespace Argonaut.Ui.Tree;

/// <summary>A link run on a tree row was clicked.</summary>
/// <param name="Row">The row it is on.</param>
/// <param name="Link">The run's <see cref="TreeRun.Link"/>: the format's token for what to do.</param>
/// <param name="Bounds">Where the clicked run is, in the surface's coordinates - what a menu or a
/// card it opens is placed against.</param>
public sealed class TreeLinkClickedEventArgs(TreeRow row, object link, Rect bounds) : EventArgs
{
    public TreeRow Row { get; } = row;

    public object Link { get; } = link;

    public Rect Bounds { get; } = bounds;
}
