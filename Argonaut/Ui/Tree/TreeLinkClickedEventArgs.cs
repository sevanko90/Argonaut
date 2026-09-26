using System;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>A link run on a tree row was clicked.</summary>
/// <param name="Row">The row it is on.</param>
/// <param name="Link">The run's <see cref="TreeRun.Link"/>: the format's token for what to do.</param>
public sealed class TreeLinkClickedEventArgs(TreeRow row, object link) : EventArgs
{
    public TreeRow Row { get; } = row;

    public object Link { get; } = link;
}
