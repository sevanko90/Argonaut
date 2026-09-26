using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>
/// The one thing a format tells <see cref="TreeSurface"/> about drawing: what text a row shows, in
/// styled runs. Indentation, expand arrows, selection, highlights, gutters and the palette are
/// the surface's. A row is asked for its runs only when it comes on screen and the result is
/// cached while it stays there, so this may decode bytes.
///
/// A painter may split each row into side-by-side panes - a comparison's two documents - by
/// giving a <see cref="PaneCount"/> above one. Each pane then has its own runs, marker and tint,
/// indented by the row's depth within its own half.
/// </summary>
public interface ITreeRowPainter
{
    /// <summary>Appends the runs of <paramref name="row"/>'s text to <paramref name="runs"/>. For
    /// a collapsed open row that includes its summary; a close row is its closing.</summary>
    void AppendRuns(in TreeRow row, List<TreeRun> runs);

    /// <summary>A short label drawn small and right-aligned before the row's expand arrow - a
    /// JSON array element's index - or null for none. A row with a marker steps in from its
    /// parent by the marker's width rather than the indent, and its children with it.</summary>
    string? Marker(in TreeRow row) => null;

    /// <summary>How many side-by-side panes each row has.</summary>
    int PaneCount => 1;

    /// <summary>Appends the runs of one pane of <paramref name="row"/>; empty when the row has
    /// nothing on that side, which also leaves its arrow undrawn there.</summary>
    void AppendPaneRuns(in TreeRow row, int pane, List<TreeRun> runs)
    {
        if (pane == 0)
            AppendRuns(row, runs);
    }

    /// <summary>One pane's marker; see <see cref="Marker"/>.</summary>
    string? PaneMarker(in TreeRow row, int pane) => pane == 0 ? Marker(row) : null;

    /// <summary>What colour one pane of <paramref name="row"/> is washed in.</summary>
    TreeRowTint PaneTint(in TreeRow row, int pane) => TreeRowTint.None;
}

/// <summary>A wash behind a pane of a row, for what a comparison found there. The surface's
/// <c>TintBrushes</c> say what colour each is.</summary>
public enum TreeRowTint : byte
{
    None,
    Added,
    Removed,
    Changed,
    Moved,
}
