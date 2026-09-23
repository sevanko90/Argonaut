using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Argonaut.Features.Raw.Editing;

namespace Argonaut.Diagnostics;

/// <summary>
/// The document drawn as two bars on one horizontal scale, which is the picture the numbers
/// elsewhere in the inspector do not give you.
///
/// The <b>top bar</b> is the row index: the whole document end to end, with each dirty span
/// marked where it sits. That answers "how much of this file is the index holding rows for" -
/// normally a sliver or two, which is the point.
///
/// The <b>bottom bar</b> is the piece table over the same scale, alternating shades per piece
/// with typed bytes picked out. Stacking them is what shows the relationship: every scratch piece
/// sits inside a span, and every span is there because of a scratch piece near it.
///
/// A span or a piece can be a handful of bytes in a multi-GB document, which is a fraction of a
/// pixel, so both are drawn at a minimum visible width. That makes the bars a map rather than a
/// proportional chart - positions are true, widths are not below the minimum - and it is the only
/// way a single keystroke's piece is visible at all.
/// </summary>
internal sealed class RawEditMapView : Control
{
    private const double BarHeight = 26;
    private const double BarGap = 10;
    private const double LabelHeight = 14;
    private const double MinimumMarkWidth = 3;

    private RawEditSnapshot? snapshot;

    /// <summary>Label colour, so the bars read in both themes.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<RawEditMapView>();

    static RawEditMapView() => AffectsRender<RawEditMapView>(ForegroundProperty);

    public RawEditMapView()
    {
        Height = (BarHeight * 2) + BarGap + (LabelHeight * 2);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public RawEditSnapshot? Snapshot
    {
        get => this.snapshot;
        set
        {
            this.snapshot = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (this.snapshot is not { DocumentLength: > 0 } state || Bounds.Width <= 0)
            return;

        double width = Bounds.Width;
        var track = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var spanBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        var originalPiece = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
        var originalPieceAlt = new SolidColorBrush(Color.FromArgb(110, 128, 128, 128));
        var scratchPiece = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        var caretBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        var labelBrush = Foreground ?? Brushes.Gray;
        var typeface = new Typeface(FontFamily.Default);

        DrawLabel(context, typeface, labelBrush, "row index — dirty spans", 0);
        double spansTop = LabelHeight;
        context.FillRectangle(track, new Rect(0, spansTop, width, BarHeight));

        foreach (var span in state.Spans)
        {
            var rect = Scale(span.StartOffset, span.EndOffset - span.StartOffset, state.DocumentLength, width, spansTop);
            context.FillRectangle(spanBrush, rect);
        }

        DrawLabel(context, typeface, labelBrush, "piece table — scratch picked out", spansTop + BarHeight + (BarGap / 2));
        double piecesTop = spansTop + BarHeight + BarGap + LabelHeight;
        context.FillRectangle(track, new Rect(0, piecesTop, width, BarHeight));

        foreach (var piece in state.Pieces)
        {
            var rect = Scale(piece.LogicalStart, piece.Length, state.DocumentLength, width, piecesTop);
            var brush = piece.FromOriginal
                ? (piece.Index % 2 == 0 ? originalPiece : originalPieceAlt)
                : scratchPiece;

            context.FillRectangle(brush, rect);
        }

        // The caret last, over both bars, because where it is relative to the spans is the thing
        // being watched while typing.
        double caretX = Math.Clamp(state.CaretOffset / (double)state.DocumentLength * width, 0, width - 1);
        context.FillRectangle(caretBrush, new Rect(Math.Floor(caretX), spansTop, 1.5, piecesTop + BarHeight - spansTop));
    }

    private static Rect Scale(long start, long length, long total, double width, double top)
    {
        double x = start / (double)total * width;
        double w = Math.Max(MinimumMarkWidth, length / (double)total * width);
        return new Rect(Math.Min(x, Math.Max(0, width - w)), top, w, BarHeight);
    }

    private static void DrawLabel(DrawingContext context, Typeface typeface, IBrush brush, string text, double y)
    {
        var layout = new Avalonia.Media.TextFormatting.TextLayout(text, typeface, 10, brush);
        layout.Draw(context, new Point(0, y));
    }

}
