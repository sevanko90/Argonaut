using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Ui.Rows;

/// <summary>
/// Draws the chips that follow a row's text - a hint, an action - the same on every surface: a
/// rounded fill, then a colour swatch or an icon, then the label, centred in a
/// <see cref="RowSurface.RowHeight"/> band. Holds the pens it draws with, so one per surface.
/// </summary>
public sealed class ChipPainter
{
    // Inner padding, height and corner, and the icon a chip leads with - sized and weighted (on
    // Lucide's 24-unit grid) to sit with the smaller interface text inside it.
    public const double Padding = 6;
    public const double Height = 17;
    public const double Radius = 4;
    private const double IconSize = 11;
    private const double IconGap = 5;
    private const double IconStroke = 2;
    private const double SwatchRadius = 2.5;

    private readonly Dictionary<IBrush, Pen> iconPens = new();
    private Pen? swatchEdgePen;

    /// <summary>A chip's whole width for its label, leading with a glyph (an icon or a swatch) or not.</summary>
    public static double WidthOf(TextLayout label, bool hasGlyph)
        => label.WidthIncludingTrailingWhitespace + 2 * Padding + (hasGlyph ? IconSize + IconGap : 0);

    /// <summary>Where a chip's fill sits in a row band.</summary>
    public static double TopIn(double rowTop) => rowTop + (RowSurface.RowHeight - Height) / 2;

    /// <summary>A chip at <paramref name="x"/> in the row band at <paramref name="rowTop"/>, filled
    /// with <paramref name="fill"/> when given, leading with <paramref name="swatch"/> or else
    /// <paramref name="icon"/> drawn in the label's <paramref name="brush"/>.</summary>
    public void Draw(DrawingContext context, double x, double rowTop, double width, TextLayout label, IBrush brush,
        IBrush? fill, Geometry? icon, IBrush? swatch)
    {
        if (fill is not null)
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(x, TopIn(rowTop), width, Height), Radius));

        double labelX = x + Padding;
        double glyphTop = rowTop + (RowSurface.RowHeight - IconSize) / 2;
        if (swatch is not null)
        {
            DrawSwatch(context, swatch, brush, new Rect(labelX, glyphTop, IconSize, IconSize));
            labelX += IconSize + IconGap;
        }
        else if (icon is not null)
        {
            double scale = IconSize / 24;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(labelX, glyphTop)))
                context.DrawGeometry(null, IconPen(brush), icon);
            labelX += IconSize + IconGap;
        }

        label.Draw(context, new Point(labelX, rowTop + Math.Max(0, (RowSurface.RowHeight - label.Height) / 2)));
    }

    /// <summary>The pens are per brush, and a theme change replaces the brushes.</summary>
    public void ForgetPens()
    {
        iconPens.Clear();
        swatchEdgePen = null;
    }

    /// <summary>
    /// A colour swatch: a checker under it, so a translucent colour shows as one, and a faint edge
    /// in the label's colour, so a colour close to the chip's own still has a shape.
    /// </summary>
    public void DrawSwatch(DrawingContext context, IBrush swatch, IBrush edge, Rect area)
    {
        var shape = new RoundedRect(area, SwatchRadius);
        using (context.PushClip(shape))
        {
            if (swatch is ISolidColorBrush { Color.A: < 255 })
            {
                double half = area.Width / 2;
                using (context.PushOpacity(0.35))
                {
                    context.FillRectangle(edge, new Rect(area.X, area.Y, half, half));
                    context.FillRectangle(edge, new Rect(area.X + half, area.Y + half, half, half));
                }
            }

            context.FillRectangle(swatch, area);
        }

        using (context.PushOpacity(0.45))
            context.DrawRectangle(null, SwatchEdgePen(edge), new RoundedRect(area.Deflate(0.5), SwatchRadius));
    }

    private Pen SwatchEdgePen(IBrush brush)
    {
        if (swatchEdgePen?.Brush != brush)
            swatchEdgePen = new Pen(brush, 1);
        return swatchEdgePen;
    }

    private Pen IconPen(IBrush brush)
    {
        if (!iconPens.TryGetValue(brush, out var pen))
            iconPens[brush] = pen = new Pen(brush, IconStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        return pen;
    }
}
