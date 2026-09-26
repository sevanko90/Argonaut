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

    /// <summary>What to show when the pointer rests on this gutter's cell for
    /// <paramref name="row"/>, or null for nothing.</summary>
    object? ToolTipFor(in TreeRow row) => null;
}

/// <summary>A gutter the user can widen or narrow by dragging its right edge.</summary>
public interface IResizableTreeGutter : ITreeGutter
{
    double MinWidth { get; }

    void Resize(double width);
}
