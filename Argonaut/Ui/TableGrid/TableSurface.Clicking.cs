using System;
using System.Collections.Generic;
using Argonaut.Engine.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace Argonaut.Ui.TableGrid;

/// <summary>
/// The surface's clickable parts, for a grid whose cells and headers do more than select: the
/// JSON array table, where a cell opens in the pane beside the grid and a header's container
/// pieces open or collapse columns. A grid that sets neither <see cref="CellClickHint"/> nor
/// <see cref="ColumnHeaders"/> - the CSV view - draws and behaves exactly as it does without them.
///
/// Nothing in a grid of values says a cell can be clicked, so a clickable cell teaches it under the
/// pointer: a tint, a hand, an "open" mark drawn over its right edge, and the hint below the value
/// in its tooltip. The mark is drawn, never laid out, so it costs the column no width. A header's
/// link pieces are drawn in the link colour with a dotted underline at rest, solid under the
/// pointer, and each says in its own tooltip what clicking it does.
/// </summary>
public partial class TableSurface
{
    /// <summary>The mark's icon, its square side.</summary>
    private const double MarkIconSize = 9;

    /// <summary>Space the mark's tint covers left of its icon, so it masks the tail of a long
    /// value rather than sitting on top of it.</summary>
    private const double MarkLead = 6;

    /// <summary>Material "open_in_full", in its 24-unit box; the drawing spans 3..21.</summary>
    private const string MarkPathData = "M21 11V3h-8l3.29 3.29-10 10L3 13v8h8l-3.29-3.29 10-10z";

    private const double MarkPathOrigin = 3;
    private const double MarkPathSpan = 18;

    private const double TipMaxWidth = 420;

    /// <summary>Headers drawn as pieces, some of them links, in place of the structure's column
    /// names; null draws the names. A different list means different columns - every width starts
    /// again from its discovered one, even where the count and the widths happen to match.</summary>
    public static readonly StyledProperty<IReadOnlyList<TableColumnHeader>?> ColumnHeadersProperty =
        AvaloniaProperty.Register<TableSurface, IReadOnlyList<TableColumnHeader>?>(nameof(ColumnHeaders));

    /// <summary>What a click on a cell opens, said in the cell's tooltip; null for a grid whose
    /// cells only select, which then wear none of the clickable cues.</summary>
    public static readonly StyledProperty<string?> CellClickHintProperty =
        AvaloniaProperty.Register<TableSurface, string?>(nameof(CellClickHint));

    /// <summary>Behind a clickable cell under the pointer, and its mark.</summary>
    public static readonly StyledProperty<IBrush?> HoverBrushProperty =
        AvaloniaProperty.Register<TableSurface, IBrush?>(nameof(HoverBrush));

    /// <summary>The link pieces of a header.</summary>
    public static readonly StyledProperty<IBrush?> LinkForegroundProperty =
        AvaloniaProperty.Register<TableSurface, IBrush?>(nameof(LinkForeground));

    private Geometry? markGeometry;
    private Pen? dottedLinkPen;
    private Pen? solidLinkPen;
    private Cursor? handCursor;
    private Cursor? resizeCursor;

    /// <summary>What the pointer is over: a cell, or a header (<see cref="Row"/> -1) and which
    /// link piece of it (<see cref="Piece"/> -1 for none).</summary>
    private readonly record struct HoverTarget(int Row, int Column, int Piece)
    {
        public bool IsHeader => Row < 0;
    }

    private HoverTarget? hoverTarget;
    private Point? pointerAt;

    public IReadOnlyList<TableColumnHeader>? ColumnHeaders
    {
        get => GetValue(ColumnHeadersProperty);
        set => SetValue(ColumnHeadersProperty, value);
    }

    public string? CellClickHint
    {
        get => GetValue(CellClickHintProperty);
        set => SetValue(CellClickHintProperty, value);
    }

    public IBrush? HoverBrush
    {
        get => GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    public IBrush? LinkForeground
    {
        get => GetValue(LinkForegroundProperty);
        set => SetValue(LinkForegroundProperty, value);
    }

    /// <summary>A cell was clicked - raised for every single click in the body, after the click
    /// has selected the row.</summary>
    public event EventHandler<TableCellPosition>? CellClicked;

    /// <summary>A link piece of a header was clicked. The surface is done with the click before
    /// this is raised, so a handler may replace the columns it was raised from.</summary>
    public event EventHandler<TableHeaderLink>? HeaderLinkClicked;

    /// <summary>Test seam: the tooltip the pointer has put up, or null.</summary>
    internal object? CurrentTip => ToolTip.GetTip(this);

    /// <summary>
    /// The columns are different ones, not the same ones relabelled: start every width again from
    /// what discovery measured. Called for a new header list - expanding a column can land on the
    /// same count and the same widths while every column now holds a different value.
    /// </summary>
    private void OnColumnHeadersChanged()
    {
        this.headers.Clear();
        this.hoverTarget = null;
        if (Structure is { } structure && this.columns is not null)
        {
            this.columns = new TableColumnLayout(OpeningWidths(structure));
            OnColumnWidthsChanged();
        }

        InvalidateVisual();
    }

    private TableColumnHeader? PiecedHeader(int column)
        => ColumnHeaders is { } pieced && column < pieced.Count ? pieced[column] : null;

    // ---- headers ------------------------------------------------------------------------

    /// <summary>A column's header text laid out at its width: its name, or its pieces read in
    /// order with the links in the link colour.</summary>
    private CellText HeaderAt(int column, TableStructure structure, TableColumnLayout layout, Typeface typeface, IBrush foreground)
    {
        if (this.headers.TryGetValue(column, out var cached))
            return cached;

        var header = PiecedHeader(column) is { } pieced
            ? LayOutPieces(pieced, layout.Width(column), typeface, foreground)
            : LayOut(structure.Columns[column].Name, layout.Width(column), typeface, FontWeight.SemiBold, foreground);
        this.headers[column] = header;
        return header;
    }

    private CellText LayOutPieces(TableColumnHeader header, double columnWidth, Typeface typeface, IBrush foreground)
    {
        var bold = new Typeface(typeface.FontFamily, weight: FontWeight.SemiBold);
        string text = ControlGlyphs.ForDisplay(string.Concat(PieceTexts(header)));
        List<ValueSpan<TextRunProperties>>? links = null;
        if (LinkForeground is { } linkBrush)
        {
            var linkRun = new GenericTextRunProperties(bold, FontSize, foregroundBrush: linkBrush);
            int start = 0;
            foreach (var piece in header.Pieces)
            {
                if (piece.IsLink && piece.Text.Length > 0)
                    (links ??= []).Add(new ValueSpan<TextRunProperties>(start, piece.Text.Length, linkRun));
                start += piece.Text.Length;
            }
        }

        var layout = new TextLayout(text, bold, FontSize, foreground,
            TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis,
            maxWidth: Math.Max(0, columnWidth - 2 * CellPaddingX), textStyleOverrides: links);

        bool trimmed = false;
        foreach (var line in layout.TextLines)
            trimmed |= line.HasCollapsed;

        return new CellText(layout, text, trimmed);
    }

    private static IEnumerable<string> PieceTexts(TableColumnHeader header)
    {
        foreach (var piece in header.Pieces)
            yield return piece.Text;
    }

    /// <summary>Underlines a pieced header's links where they were drawn: dotted at rest, solid
    /// for the one under the pointer.</summary>
    private void DrawLinkUnderlines(DrawingContext context, int column, CellText header, double x)
    {
        if (PiecedHeader(column) is not { } pieced || LinkForeground is not { } brush)
            return;

        if (this.solidLinkPen?.Brush != brush)
        {
            this.solidLinkPen = new Pen(brush, 1);
            this.dottedLinkPen = new Pen(brush, 1, new DashStyle([1, 2], 0));
        }

        double top = Math.Max(0, (HeaderHeight - header.Layout.Height) / 2);
        int start = 0;
        for (int p = 0; p < pieced.Pieces.Count; p++)
        {
            var piece = pieced.Pieces[p];
            if (piece.IsLink)
            {
                bool hovered = this.hoverTarget is { IsHeader: true } at && at.Column == column && at.Piece == p;
                var pen = hovered ? this.solidLinkPen : this.dottedLinkPen!;
                foreach (var hit in header.Layout.HitTestTextRange(start, piece.Text.Length))
                {
                    double y = Math.Round(top + hit.Y + hit.Height) - 1.5;
                    context.DrawLine(pen, new Point(x + hit.X, y), new Point(x + hit.X + hit.Width, y));
                }
            }

            start += piece.Text.Length;
        }
    }

    /// <summary>The link piece of a header a point is on, or -1 - on the text itself, not just
    /// beside it, so the gap after a short header is not a link.</summary>
    private int LinkPieceAt(int column, Point point)
    {
        if (PiecedHeader(column) is not { } pieced || this.columns is not { } layout || Structure is not { } structure)
            return -1;

        var header = HeaderAt(column, structure, layout, new Typeface(FontFamily), Foreground ?? Brushes.Black);
        double left = this.gutterWidth + layout.Left(column) - PanOffset + CellPaddingX;
        double top = Math.Max(0, (HeaderHeight - header.Layout.Height) / 2);
        var hit = header.Layout.HitTestPoint(new Point(point.X - left, point.Y - top));
        if (!hit.IsInside)
            return -1;

        int position = hit.TextPosition;
        int start = 0;
        for (int p = 0; p < pieced.Pieces.Count; p++)
        {
            var piece = pieced.Pieces[p];
            if (position < start + piece.Text.Length)
                return piece.IsLink ? p : -1;
            start += piece.Text.Length;
        }

        return -1;
    }

    // ---- clickable cells ----------------------------------------------------------------

    /// <summary>The clickable cell under the pointer as the grid now lies - worked out from where
    /// the pointer is rather than remembered, so a wheel scroll under a still pointer moves the
    /// tint onto the cell that is now there.</summary>
    private (int Row, int Column)? HoveredClickableCell()
        => CellClickHint is not null && this.pointerAt is { } point && EdgeAt(point) < 0 ? CellAtPoint(point) : null;

    /// <summary>The "this opens something" mark over a hovered cell's right edge, on the hover
    /// tint so it masks the value's tail rather than sitting on it.</summary>
    private void DrawOpenMark(DrawingContext context, double cellRight, double rowTop)
    {
        double iconLeft = cellRight - CellPaddingX - MarkIconSize;
        if (HoverBrush is { } tint)
            context.FillRectangle(tint, new Rect(iconLeft - MarkLead, rowTop, MarkLead + MarkIconSize + CellPaddingX, RowHeight));

        this.markGeometry ??= Geometry.Parse(MarkPathData);
        double scale = MarkIconSize / MarkPathSpan;
        double iconTop = rowTop + (RowHeight - MarkIconSize) / 2;
        var place = Matrix.CreateTranslation(-MarkPathOrigin, -MarkPathOrigin)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(iconLeft, iconTop);
        using (context.PushTransform(place))
            context.DrawGeometry(GutterForeground ?? Foreground ?? Brushes.Gray, null, this.markGeometry);
    }

    // ---- pointer ------------------------------------------------------------------------

    /// <summary>What a point is over, for the tooltip, the cursor and the hover cues.</summary>
    private HoverTarget? TargetAt(Point point)
    {
        if (point.X < this.gutterWidth || this.columns is not { } layout)
            return null;

        if (point.Y < HeaderHeight)
        {
            int column = layout.ColumnAt(point.X - this.gutterWidth + PanOffset);
            return column >= 0 ? new HoverTarget(-1, column, LinkPieceAt(column, point)) : null;
        }

        return CellAtPoint(point) is { } cell ? new HoverTarget(cell.Row, cell.Column, -1) : null;
    }

    private void TrackPointer(Point? at)
    {
        this.pointerAt = at;
        if (at is not { } point)
        {
            HoverOver(null);
            return;
        }

        bool onEdge = EdgeAt(point) >= 0;
        var target = onEdge ? null : TargetAt(point);
        bool clickable = target is { IsHeader: true, Piece: >= 0 } || (target is { IsHeader: false } && CellClickHint is not null);

        Cursor = onEdge ? this.resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast)
            : clickable ? this.handCursor ??= new Cursor(StandardCursorType.Hand)
            : Cursor.Default;

        HoverOver(target);
    }

    private void HoverOver(HoverTarget? target)
    {
        if (target == this.hoverTarget)
            return;

        this.hoverTarget = target;
        ToolTip.SetTip(this, null);
        ToolTip.SetTip(this, target is { } at ? TipFor(at) : null);
        if (CellClickHint is not null || ColumnHeaders is not null)
            InvalidateVisual();
    }

    /// <summary>
    /// A header's tip is its link's action, or the whole header where it is not a link. A cell's
    /// is its value when that is cut off - and, when cells open something, the value whenever
    /// there is one, then the hint, muted and set apart so it reads as chrome rather than value.
    /// An empty cell still gets the hint: a missing property is still a cell worth clicking.
    /// </summary>
    private object? TipFor(HoverTarget at)
    {
        if (at.IsHeader)
        {
            var pieced = PiecedHeader(at.Column);
            return at.Piece >= 0 && pieced is not null ? pieced.Pieces[at.Piece].LinkTip : pieced?.Tip;
        }

        if (CellClickHint is not { } hint)
            return this.cells.TryGetValue((at.Row, at.Column), out var text) && text.Trimmed ? text.Text : null;

        string value = Rows?[at.Row] is TableRow row && at.Column < row.Cells.Count
            ? ControlGlyphs.ForDisplay(row.Cells[at.Column].Text)
            : string.Empty;
        return HintTip(value, hint);
    }

    private Control HintTip(string value, string hint)
    {
        var tip = new StackPanel();
        if (value.Length > 0)
        {
            tip.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = TipMaxWidth, FontFamily = FontFamily });
            tip.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6, 0, 5), Background = GridLineBrush });
        }

        var hintText = new TextBlock { Text = hint, FontSize = 11 };
        if (GutterForeground is { } muted)
            hintText.Foreground = muted;
        tip.Children.Add(hintText);
        return tip;
    }

    /// <summary>A click on a header's link, or on a cell: the surface's own work - the row
    /// selected, the hover forgotten - is done before the event, so its handler is free to
    /// replace the columns or the rows.</summary>
    private bool RaiseClick(Point point, int clickCount)
    {
        if (TargetAt(point) is not { } at)
            return false;

        if (at.IsHeader)
        {
            if (at.Piece < 0)
                return false;

            HoverOver(null);
            HeaderLinkClicked?.Invoke(this, new TableHeaderLink(at.Column, at.Piece));
            return true;
        }

        if (clickCount == 1)
            CellClicked?.Invoke(this, new TableCellPosition(at.Row, at.Column));
        return false;
    }
}

/// <summary>A cell of a grid: its row and its column.</summary>
public readonly record struct TableCellPosition(int Row, int Column);
