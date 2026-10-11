using System;
using System.Collections.Generic;
using Argonaut.Engine.Hints;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Rows;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Features.Raw.Hints;

/// <summary>Where the pointer is over a hint: the value, and the rectangle (in surface
/// coordinates) of the chip or the marked text it is over.</summary>
public sealed record RawHintHit(int RowIndex, RawValue Value, Rect Bounds, bool IsChip);

/// <summary>
/// The raw surface's value hints for the rows on screen: which values each row marks, the chips
/// after a row's text, and where the pointer is over one. Keeps per-row state with the same
/// lifetime as the surface's layouts - cleared wherever they are - and the carry from row to row
/// beside the lexer's.
///
/// A chip is drawn as the tree draws it, a gap after the row's text. A value marked in place gets
/// a line under it that never moves the text, the wrapping or the caret: in its own colour for a
/// colour, so the swatch is the underline, and a dotted line in the accent otherwise, solid while
/// hovered.
/// </summary>
public sealed class RawHintLayer
{
    private const double ChipGap = 10;
    private const double ChipSpacing = 6;
    private const double SwatchUnderline = 3;

    private sealed record Chip(RawHintMark Mark, TextLayout Label, double X, double Width, Geometry? Icon, IBrush? Swatch);

    private sealed record RowHints(List<RawHintMark> Marks, List<Chip>? Chips);

    private readonly Dictionary<int, RowHints> rows = new();
    private readonly Dictionary<int, RawHintCarry> carries = new();
    private readonly Dictionary<IBrush, Pen> dottedPens = new();
    private readonly Dictionary<IBrush, Pen> solidPens = new();
    private readonly ChipPainter chips = new();
    private RawHintHit? hovered;

    /// <summary>The hints being shown; null shows none.</summary>
    public RawValueHints? Hints { get; set; }

    public IBrush? ChipBackground { get; set; }

    public IBrush? ChipHoverBackground { get; set; }

    /// <summary>A chip's label and icon.</summary>
    public IBrush? ChipForeground { get; set; }

    /// <summary>The line under a value marked in place, dotted until hovered.</summary>
    public IBrush? MarkBrush { get; set; }

    public IReadOnlyDictionary<TreeRunIcon, Geometry>? Icons { get; set; }

    public FontFamily? ChipFontFamily { get; set; }

    /// <summary>The value under the pointer, if any.</summary>
    public RawHintHit? Hovered => hovered;

    /// <summary>Forgets every row's hints - their text or layout changed.</summary>
    public void Clear()
    {
        rows.Clear();
        carries.Clear();
        hovered = null;
    }

    /// <summary>A theme change: the pens hold old brushes.</summary>
    public void ForgetPens()
    {
        dottedPens.Clear();
        solidPens.Clear();
        chips.ForgetPens();
    }

    /// <summary>What a row hands the next one, when it has been marked or carried.</summary>
    public RawHintCarry? CarryAfter(int rowIndex) => carries.TryGetValue(rowIndex, out var carry) ? carry : null;

    /// <summary>Works out a row's hints from its lexer spans and keeps them for drawing.</summary>
    public void MarkRow(int rowIndex, in RawHintRow row, RawHintCarry entry, List<RawStyledSpan> spans, Typeface typeface, double fontSize)
    {
        if (Hints is not { } hints)
            return;

        var marks = new List<RawHintMark>();
        carries[rowIndex] = hints.MarkRow(row, entry, spans, marks);
        hints.MarkWords(row, spans, marks);
        rows[rowIndex] = new RowHints(marks, LayOutChips(marks, typeface, fontSize));
    }

    /// <summary>Works out a row's hints word by word, for a row no lexer reads.</summary>
    public void MarkWords(int rowIndex, in RawHintRow row)
    {
        if (Hints is not { } hints)
            return;

        var marks = new List<RawHintMark>();
        hints.MarkWords(row, null, marks);
        rows[rowIndex] = new RowHints(marks, null);
    }

    /// <summary>Works out only what a row hands on, for a row lexed to reach a later one.</summary>
    public void CarryRow(int rowIndex, in RawHintRow row, RawHintCarry entry, List<RawStyledSpan> spans)
    {
        if (Hints is not { } hints)
            return;

        carries[rowIndex] = hints.MarkRow(row, entry, spans, null);
    }

    /// <summary>A row's marks as last worked out, for tests.</summary>
    internal IReadOnlyList<RawHintMark> MarksOf(int rowIndex)
        => rows.TryGetValue(rowIndex, out var hints) ? hints.Marks : Array.Empty<RawHintMark>();

    /// <summary>Where a row's first chip sits past its text, for tests.</summary>
    internal Rect? ChipBounds(int rowIndex, double textEnd, double rowTop)
        => rows.TryGetValue(rowIndex, out var hints) && hints.Chips is [var chip, ..]
            ? new Rect(textEnd + chip.X, ChipPainter.TopIn(rowTop), chip.Width, ChipPainter.Height)
            : null;

    /// <summary>Where a mark's first stretch of text sits, for tests.</summary>
    internal static Rect? MarkBounds(TextLayout layout, Point textOrigin, RawHintMark mark)
    {
        foreach (var rect in layout.HitTestTextRange(mark.Start, mark.End - mark.Start))
            return rect.Translate(new Vector(textOrigin.X, textOrigin.Y));
        return null;
    }

    /// <summary>How far past a row's text its chips reach, for the pan range.</summary>
    public double TrailingWidth(int rowIndex)
        => rows.TryGetValue(rowIndex, out var hints) && hints.Chips is { Count: > 0 } laid
            ? laid[^1].X + laid[^1].Width
            : 0;

    /// <summary>Keeps the rows on screen and the band a lookback reaches above them.</summary>
    public void Prune(int firstRow, int lastRow, int lookback)
    {
        if (rows.Count + carries.Count <= (lastRow - firstRow + 1) * 4 + lookback)
            return;

        Remove(rows, firstRow, lastRow);
        Remove(carries, firstRow - lookback, lastRow);
    }

    private static void Remove<T>(Dictionary<int, T> map, int first, int last)
    {
        var outside = new List<int>();
        foreach (int rowIndex in map.Keys)
        {
            if (rowIndex < first || rowIndex > last)
                outside.Add(rowIndex);
        }

        foreach (int rowIndex in outside)
            map.Remove(rowIndex);
    }

    private List<Chip>? LayOutChips(List<RawHintMark> marks, Typeface textTypeface, double fontSize)
    {
        List<Chip>? laid = null;
        double x = ChipGap;
        var brush = ChipForeground ?? Brushes.Gray;
        var typeface = ChipFontFamily is { } family ? new Typeface(family) : textTypeface;
        foreach (var mark in marks)
        {
            if (!mark.IsChip || !mark.EndsHere)
                continue;

            var hint = mark.Hint;
            var label = new TextLayout(hint.Text, typeface, Math.Max(6, fontSize - 1), brush);
            var swatch = ValueHintLook.SwatchOf(hint) is { } colour ? new ImmutableSolidColorBrush(colour) : null;
            var icon = swatch is null && Icons is not null && Icons.TryGetValue(ValueHintLook.IconOf(hint.Symbol), out var glyph) ? glyph : null;
            double width = ChipPainter.WidthOf(label, swatch is not null || icon is not null);
            (laid ??= new()).Add(new Chip(mark, label, x, width, icon, swatch));
            x += width + ChipSpacing;
        }

        return laid;
    }

    /// <summary>
    /// Draws a row's hints: the line under each value marked in place, then the chips after the
    /// text at <paramref name="textEnd"/>. An action chip shows only on a row with the user's
    /// attention, <paramref name="showsActions"/>.
    /// </summary>
    public void Draw(DrawingContext context, int rowIndex, TextLayout layout, Point textOrigin, double rowTop, double textEnd, bool showsActions)
    {
        if (!rows.TryGetValue(rowIndex, out var hints))
            return;

        var brush = ChipForeground ?? Brushes.Gray;
        foreach (var mark in hints.Marks)
        {
            if (mark.IsChip || mark.End <= mark.Start)
                continue;

            bool isHot = hovered is { IsChip: false } hit && hit.RowIndex == rowIndex && hit.Value == mark.Value;
            foreach (var rect in layout.HitTestTextRange(mark.Start, mark.End - mark.Start))
            {
                var area = rect.Translate(new Vector(textOrigin.X, textOrigin.Y));
                if (ValueHintLook.SwatchOf(mark.Hint) is { } colour)
                {
                    context.FillRectangle(new ImmutableSolidColorBrush(colour), new Rect(area.X, area.Bottom - SwatchUnderline + 1, area.Width, SwatchUnderline));
                    continue;
                }

                double y = Math.Round(area.Bottom) + 0.5;
                var markBrush = MarkBrush ?? brush;
                context.DrawLine(isHot ? SolidPen(markBrush) : DottedPen(markBrush), new Point(area.X, y), new Point(area.Right, y));
            }
        }

        if (hints.Chips is not { } laid)
            return;

        foreach (var chip in laid)
        {
            if (chip.Mark.Hint.Emphasis == ValueHintEmphasis.Action && !showsActions)
                continue;

            bool isHot = hovered is { IsChip: true } hit && hit.RowIndex == rowIndex && hit.Value == chip.Mark.Value;
            var fill = isHot ? ChipHoverBackground ?? ChipBackground : ChipBackground;
            chips.Draw(context, textEnd + chip.X, rowTop, chip.Width, chip.Label, brush, fill, chip.Icon, chip.Swatch);
        }
    }

    /// <summary>
    /// The hint at <paramref name="point"/> on a row: a chip after its text, or a value marked in
    /// it. Sets <see cref="Hovered"/>, and says whether that changed, so the caller redraws only
    /// when something lit up or went out.
    /// </summary>
    public bool HitTest(int rowIndex, TextLayout layout, Point textOrigin, double rowTop, double textEnd, bool showsActions, Point point, out RawHintHit? hit)
    {
        hit = Find(rowIndex, layout, textOrigin, rowTop, textEnd, showsActions, point);
        bool changed = hit?.RowIndex != hovered?.RowIndex || hit?.Value != hovered?.Value || hit?.IsChip != hovered?.IsChip;
        hovered = hit;
        return changed;
    }

    /// <summary>The pointer left the rows: nothing is hovered.</summary>
    public bool ClearHover()
    {
        bool changed = hovered is not null;
        hovered = null;
        return changed;
    }

    private RawHintHit? Find(int rowIndex, TextLayout layout, Point textOrigin, double rowTop, double textEnd, bool showsActions, Point point)
    {
        if (!rows.TryGetValue(rowIndex, out var hints))
            return null;

        if (hints.Chips is { } laid)
        {
            foreach (var chip in laid)
            {
                if (chip.Mark.Hint.Emphasis == ValueHintEmphasis.Action && !showsActions)
                    continue;

                var bounds = new Rect(textEnd + chip.X, ChipPainter.TopIn(rowTop), chip.Width, ChipPainter.Height);
                if (bounds.Contains(point))
                    return new RawHintHit(rowIndex, chip.Mark.Value, bounds, IsChip: true);
            }
        }

        var local = point - textOrigin;
        foreach (var mark in hints.Marks)
        {
            if (mark.IsChip || mark.End <= mark.Start)
                continue;

            foreach (var rect in layout.HitTestTextRange(mark.Start, mark.End - mark.Start))
            {
                if (rect.Contains(new Point(local.X, local.Y)))
                    return new RawHintHit(rowIndex, mark.Value, rect.Translate(new Vector(textOrigin.X, textOrigin.Y)), IsChip: false);
            }
        }

        return null;
    }

    private Pen DottedPen(IBrush brush)
    {
        if (!dottedPens.TryGetValue(brush, out var pen))
            dottedPens[brush] = pen = new Pen(brush, 1, new DashStyle([1, 2], 0));
        return pen;
    }

    private Pen SolidPen(IBrush brush)
    {
        if (!solidPens.TryGetValue(brush, out var pen))
            solidPens[brush] = pen = new Pen(brush, 1);
        return pen;
    }
}
