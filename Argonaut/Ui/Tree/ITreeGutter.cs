using Argonaut.Engine.Indexing.Trees;
using Avalonia;
using Avalonia.Media;

namespace Argonaut.Ui.Tree;

/// <summary>The text style a gutter draws in: the surface's font, in its gutter brush.</summary>
public readonly record struct TreeGutterStyle(Typeface Typeface, double FontSize, IBrush Brush);

/// <summary>
/// A fixed-width column to the left of a tree's rows, pinned while the rows pan: a schema label,
/// a line number, a marker. The surface lays gutters out left to right in the order given and
/// asks each to draw its cell for every row on screen.
/// </summary>
public interface ITreeGutter
{
    double Width { get; }

    void Draw(DrawingContext context, in TreeRow row, Rect cell, in TreeGutterStyle style);
}
