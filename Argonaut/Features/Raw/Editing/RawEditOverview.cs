using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Features.Raw.Editing;

/// <summary>
/// One mark on the edit overview: the pixel rows it covers, inclusive, and the document offset a
/// click on it jumps to - the first edited byte it stands for.
/// </summary>
internal readonly record struct RawEditMark(int Top, int Bottom, long Offset);

/// <summary>
/// Where the edits are, as marks on a strip the height of the view: the piece table's picture in
/// the debug inspector, turned on its side and put where a scrollbar's reader looks. On a
/// multi-GB document an edit a few screens away is otherwise impossible to find again.
///
/// Positions are by <i>row</i>, not by byte, because the strip sits beside the vertical scrollbar
/// and has to agree with it: a long line is thousands of rows and a byte scale would squash
/// everything after it. Mapping every edit to a row would be a lookup per piece, so the
/// computation works per pixel instead - once a pixel is marked, every edit whose bytes fall
/// before the first row of the next pixel is skipped without a lookup. That bounds the lookups
/// by the strip's height, however many places were edited.
/// </summary>
internal static class RawEditOverviewMarks
{
    public static void Compute(RawPieceTable document, IRawRowIndex rows, int height, List<RawEditMark> marks)
    {
        marks.Clear();
        if (height <= 0 || document.IsUnedited)
            return;

        int rowCount = rows.RowCount;
        if (rowCount == 0)
        {
            // Everything deleted: there is one place left to point at.
            if (document.EnumerateEditedRanges().MoveNext())
                marks.Add(new RawEditMark(0, 0, 0));
            return;
        }

        long skipUntil = long.MinValue; // offsets below this land in a pixel already marked
        foreach (var (start, end) in document.EnumerateEditedRanges())
        {
            long last = Math.Max(start, end - 1); // the range's last byte, or the seam itself
            if (last < skipUntil)
                continue;

            long from = Math.Max(start, skipUntil);
            int top = PixelOf(RowAt(rows, from, rowCount), rowCount, height);
            int bottom = Math.Max(top, PixelOf(RowAt(rows, last, rowCount), rowCount, height));

            if (marks.Count > 0 && top <= marks[^1].Bottom + 1)
                marks[^1] = marks[^1] with { Bottom = Math.Max(bottom, marks[^1].Bottom) };
            else
                marks.Add(new RawEditMark(top, bottom, from));

            long nextRow = ((long)(bottom + 1) * rowCount + height - 1) / height;
            skipUntil = nextRow >= rowCount ? long.MaxValue : rows.GetRowInfo((int)nextRow).Start;
        }
    }

    /// <summary>The row holding <paramref name="offset"/>; the last row for the end of the
    /// document, which a seam left by deleting the tail sits at.</summary>
    private static int RowAt(IRawRowIndex rows, long offset, int rowCount)
        => rows.RowForOffset(offset) ?? rowCount - 1;

    private static int PixelOf(int row, int rowCount, int height)
        => (int)Math.Min((long)row * height / rowCount, height - 1);
}

/// <summary>
/// The strip beside the raw view's scrollbar that draws <see cref="RawEditOverviewMarks"/>, and
/// jumps to an edit when one is clicked.
///
/// Marks are recomputed lazily, at the next render after <see cref="Refresh"/>, so a burst of
/// keystrokes inside one frame pays for one computation rather than one each.
/// </summary>
internal sealed class RawEditOverview : Control
{
    /// <summary>A mark is drawn at least this tall, or a single keystroke would be a fraction of
    /// a pixel on a multi-GB document - positions are true, heights are not below this.</summary>
    private const double MinimumMarkHeight = 3;

    /// <summary>How far off a mark a click may land and still count as on it.</summary>
    private const double ClickSlop = 3;

    public static readonly StyledProperty<IBrush?> MarkBrushProperty =
        AvaloniaProperty.Register<RawEditOverview, IBrush?>(nameof(MarkBrush));

    private readonly List<RawEditMark> marks = new();
    private RawPieceTable? document;
    private IRawRowIndex? rows;
    private bool stale = true;
    private int computedHeight = -1;

    static RawEditOverview() => AffectsRender<RawEditOverview>(MarkBrushProperty);

    public RawEditOverview()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public IBrush? MarkBrush
    {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    /// <summary>Raised with the offset of the first edited byte of the mark clicked.</summary>
    public event EventHandler<long>? EditChosen;

    /// <summary>The marks for the current document and height, computed if stale.</summary>
    internal IReadOnlyList<RawEditMark> Marks
    {
        get
        {
            EnsureMarks();
            return this.marks;
        }
    }

    /// <summary>Points the strip at an edited document, or at nothing.</summary>
    public void Show(RawPieceTable? editedDocument, IRawRowIndex? rowIndex)
    {
        this.document = editedDocument;
        this.rows = rowIndex;
        Refresh();
    }

    /// <summary>The document changed; recompute at the next render.</summary>
    public void Refresh()
    {
        this.stale = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureMarks();

        // Filled so the whole strip takes clicks, not only the marks' pixels.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var brush = MarkBrush ?? Brushes.Orange;
        foreach (var mark in this.marks)
        {
            double markHeight = Math.Max(MinimumMarkHeight, mark.Bottom - mark.Top + 1);
            double top = Math.Min(mark.Top, Math.Max(0, Bounds.Height - markHeight));
            context.FillRectangle(brush, new Rect(1, top, Math.Max(0, Bounds.Width - 2), markHeight));
        }
    }

    private void EnsureMarks()
    {
        int height = (int)Bounds.Height;
        if (!this.stale && height == this.computedHeight)
            return;

        if (this.document is not null && this.rows is not null)
            RawEditOverviewMarks.Compute(this.document, this.rows, height, this.marks);
        else
            this.marks.Clear();

        this.stale = false;
        this.computedHeight = height;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        EnsureMarks();

        double y = e.GetPosition(this).Y;
        RawEditMark? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (var mark in this.marks)
        {
            double distance = y < mark.Top ? mark.Top - y : y > mark.Bottom + MinimumMarkHeight ? y - mark.Bottom - MinimumMarkHeight : 0;
            if (distance <= ClickSlop && distance < nearestDistance)
            {
                nearest = mark;
                nearestDistance = distance;
            }
        }

        if (nearest is { } chosen)
        {
            EditChosen?.Invoke(this, chosen.Offset);
            e.Handled = true;
        }
    }
}
