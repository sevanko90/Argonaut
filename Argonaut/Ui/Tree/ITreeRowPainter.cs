using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>
/// The one thing a format tells <see cref="TreeSurface"/> about drawing: what text a row shows, in
/// styled runs. Indentation, expand arrows, selection, highlights, gutters and the palette are
/// the surface's. A row is asked for its runs only when it comes on screen and the result is
/// cached while it stays there, so this may decode bytes.
/// </summary>
public interface ITreeRowPainter
{
    /// <summary>Appends the runs of <paramref name="row"/>'s text to <paramref name="runs"/>. For
    /// a collapsed open row that includes its summary; a close row is its closing.</summary>
    void AppendRuns(in TreeRow row, List<TreeRun> runs);

    /// <summary>A short label drawn small and right-aligned before the row's expand arrow - a
    /// JSON array element's index - or null for none. A row with a marker is set that much
    /// further in than its siblings without one.</summary>
    string? Marker(in TreeRow row) => null;
}
