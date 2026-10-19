using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Text;
using Argonaut.Ui.Find;
using Argonaut.Ui.Rows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Ui.TableGrid;

/// <summary>
/// A grid that draws itself: a pinned header of column names across the top, a pinned gutter of
/// line labels down the left, and the cells between them - only the rows and columns on screen,
/// with no control per cell. What the CSV view and the JSON array table are drawn with.
///
/// Rows are <see cref="RowSurface.RowHeight"/> tall and their count is known, so the scroll is
/// <see cref="RowSurface"/>'s exact model: the rows on screen are arithmetic on the offset, and a
/// dragged thumb lands on the row it points at. Columns are placed by a
/// <see cref="TableColumnLayout"/>, so the columns on screen are a binary search on the pan.
/// Panning moves the cells and the header together and leaves the gutter where it is.
///
/// The gutter labels each row as its document's <see cref="LineNumbering"/> says - the file's line
/// number, or its offset for a part of a file taken by bytes - the same choice every view makes.
///
/// Rows are banded - every other one shaded - with a line between columns all the way down, so a
/// row reads across a wide grid and a column down a long one.
///
/// What it does, like the grid it replaces: a click selects a row and the arrow and page keys
/// move the selection; every occurrence of the find term in a cell or a column name sits on a
/// pill; a value too long for its column is cut with an ellipsis and shown whole in a tooltip; a
/// column's right edge in the header drags to resize it, and a double-click there fits it to the
/// widest value among the rows already read (<see cref="IColumnFitSource"/>). A grid whose cells
/// and headers do more than select - the JSON array table's - makes them clickable; see the
/// partial that does it.
/// </summary>
public partial class TableSurface : RowSurface
{
    /// <summary>Height of the header row of column names.</summary>
    public const double HeaderHeight = RowHeight + 4;

    /// <summary>Space between a cell's edge and its text, either side.</summary>
    public const double CellPaddingX = 6;

    private const double GutterGap = 10;
    private const double MinGutterWidth = 36;

    /// <summary>How near a column's right edge the pointer must be, either side, to grab it.</summary>
    private const double ResizeGrip = 4;

    /// <summary>Narrowest a drag can make a column, so its edge can always be grabbed again.</summary>
    private const double MinColumnWidth = 3 * CellPaddingX;

    private static readonly char[] WidestCharacters = ['0', 'W', 'm', '{'];

    public static readonly StyledProperty<IList?> RowsProperty =
        AvaloniaProperty.Register<TableSurface, IList?>(nameof(Rows));

    public static readonly StyledProperty<TableStructure?> StructureProperty =
        AvaloniaProperty.Register<TableSurface, TableStructure?>(nameof(Structure));

    public static readonly StyledProperty<LineNumbering> LineNumberingProperty =
        AvaloniaProperty.Register<TableSurface, LineNumbering>(nameof(LineNumbering), defaultValue: LineNumbering.FromStart);

    /// <summary>The widest label the gutter will show, to size it by; null hides the gutter.</summary>
    public static readonly StyledProperty<string?> WidestGutterLabelProperty =
        AvaloniaProperty.Register<TableSurface, string?>(nameof(WidestGutterLabel));

    public static readonly StyledProperty<string?> HighlightTermProperty =
        AvaloniaProperty.Register<TableSurface, string?>(nameof(HighlightTerm));

    /// <summary>The selected row, or -1.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<TableSurface, int>(nameof(SelectedIndex), defaultValue: -1);

    /// <summary>The find match find is on, marked apart from the others; null for none.</summary>
    public static readonly StyledProperty<TableMatch?> CurrentMatchProperty =
        AvaloniaProperty.Register<TableSurface, TableMatch?>(nameof(CurrentMatch));

    public static readonly StyledProperty<IColumnFitSource?> FitSourceProperty =
        AvaloniaProperty.Register<TableSurface, IColumnFitSource?>(nameof(FitSource));

    /// <summary>The lines between columns, under the header and beside the gutter.</summary>
    public static readonly StyledProperty<IBrush?> GridLineBrushProperty =
        AvaloniaProperty.Register<TableSurface, IBrush?>(nameof(GridLineBrush));

    public static readonly StyledProperty<IBrush?> HeaderBackgroundProperty =
        AvaloniaProperty.Register<TableSurface, IBrush?>(nameof(HeaderBackground));

    /// <summary>Behind every other row, so a row reads across a wide grid; null draws none.</summary>
    public static readonly StyledProperty<IBrush?> RowBandBrushProperty =
        AvaloniaProperty.Register<TableSurface, IBrush?>(nameof(RowBandBrush));

    static TableSurface()
    {
        AffectsRender<TableSurface>(SelectedIndexProperty, HighlightTermProperty, GridLineBrushProperty, HeaderBackgroundProperty,
            RowBandBrushProperty, CurrentMatchProperty, CellClickHintProperty, HoverBrushProperty, LinkForegroundProperty);
    }

    // Cell text laid out at its column's width, for the rows and columns on screen. Dropped when
    // a width, the font or the rows change; pruned to the rows on screen as they scroll.
    private readonly Dictionary<(int Row, int Column), CellText> cells = new();
    private readonly Dictionary<int, CellText> headers = new();
    private readonly List<(int Row, int Column)> pruning = new();

    private TableColumnLayout? columns;
    private INotifyCollectionChanged? subscribedRows;
    private (int First, int Last) cachedRows = (0, -1);
    private double scrollTop;

    // A row ShowRowAtTop is still waiting to put at the top, until the rows reach it.
    private int? pendingTopRow;
    private double gutterWidth;
    private double charAdvance;
    private Pen? gridPen;

    private int resizingColumn = -1;
    private double resizeStartX;
    private double resizeStartWidth;

    public IList? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public TableStructure? Structure
    {
        get => GetValue(StructureProperty);
        set => SetValue(StructureProperty, value);
    }

    public LineNumbering LineNumbering
    {
        get => GetValue(LineNumberingProperty);
        set => SetValue(LineNumberingProperty, value);
    }

    public string? WidestGutterLabel
    {
        get => GetValue(WidestGutterLabelProperty);
        set => SetValue(WidestGutterLabelProperty, value);
    }

    public string? HighlightTerm
    {
        get => GetValue(HighlightTermProperty);
        set => SetValue(HighlightTermProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public TableMatch? CurrentMatch
    {
        get => GetValue(CurrentMatchProperty);
        set => SetValue(CurrentMatchProperty, value);
    }

    public IColumnFitSource? FitSource
    {
        get => GetValue(FitSourceProperty);
        set => SetValue(FitSourceProperty, value);
    }

    public IBrush? GridLineBrush
    {
        get => GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public IBrush? HeaderBackground
    {
        get => GetValue(HeaderBackgroundProperty);
        set => SetValue(HeaderBackgroundProperty, value);
    }

    public IBrush? RowBandBrush
    {
        get => GetValue(RowBandBrushProperty);
        set => SetValue(RowBandBrushProperty, value);
    }

    /// <summary>The columns' placement, for a host that needs to know where one is.</summary>
    public TableColumnLayout? Columns => this.columns;

    /// <summary>The gutter's width, which the cells start after.</summary>
    public double GutterWidth => this.gutterWidth;

    /// <summary>Test seam: cell layouts held, which must stay a screen's worth however far the grid
    /// is scrolled.</summary>
    internal int CachedCellCount => this.cells.Count;

    private int RowCount => Rows?.Count ?? 0;

    private double BodyHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    private double ExtentHeight => RowCount * RowHeight;

    private double MaxScrollTop => Math.Max(0, ExtentHeight - BodyHeight);

    // ---- reveal -------------------------------------------------------------------------

    /// <summary>
    /// Brings <paramref name="row"/> on screen centred, unless it is already wholly on screen -
    /// what a search reveal does, so the hit lands where the eye is with context either side.
    /// </summary>
    public void RevealRow(int row)
    {
        this.pendingTopRow = null;
        if ((uint)row >= (uint)RowCount)
            return;

        double top = row * RowHeight;
        if (top >= this.scrollTop && top + RowHeight <= this.scrollTop + BodyHeight)
            return;

        SetVerticalOffset(top - (BodyHeight - RowHeight) / 2);
    }

    /// <summary>
    /// Scrolls so <paramref name="row"/> is the first row on screen - putting a view back where it
    /// was scrolled to, as a filter reload does. Held until the rows reach far enough to allow it,
    /// since a document still indexing can be short of a screen past the row; the user's own
    /// scrolling, a click or a key abandons it, as does a reveal.
    /// </summary>
    public void ShowRowAtTop(int row)
    {
        this.pendingTopRow = row;
        ApplyPendingTopRow();
    }

    private void ApplyPendingTopRow()
    {
        if (this.pendingTopRow is not int row || row >= RowCount)
            return;

        double top = row * RowHeight;
        SetVerticalOffset(top);
        if (Math.Abs(this.scrollTop - top) < 0.5)
            this.pendingTopRow = null;
    }

    /// <summary>The rows from the first to the last at least partly on screen, or (0, -1) when
    /// none are.</summary>
    public (int First, int Last) RowsOnScreen()
    {
        int count = RowCount;
        if (count == 0 || BodyHeight <= 0)
            return (0, -1);

        int first = Math.Clamp((int)(this.scrollTop / RowHeight), 0, count - 1);
        int last = Math.Clamp((int)Math.Ceiling((this.scrollTop + BodyHeight) / RowHeight) - 1, first, count - 1);
        return (first, last);
    }

    /// <summary>Pans just enough to bring <paramref name="column"/> wholly into view, or its left
    /// edge when it is wider than the view.</summary>
    public void RevealColumn(int column)
    {
        if (this.columns is not { } layout || (uint)column >= (uint)layout.Count)
            return;

        double left = layout.Left(column);
        double right = layout.Right(column);
        if (left < PanOffset)
            RequestPan(left);
        else if (right > PanOffset + PanViewportWidth)
            RequestPan(Math.Min(left, right - PanViewportWidth));
    }

    // ---- scrolling ----------------------------------------------------------------------

    public override double ScrollFraction => ExtentHeight <= 0 ? 0 : this.scrollTop / ExtentHeight;

    public override double ViewportFraction => ExtentHeight <= 0 ? 1 : Math.Min(1, BodyHeight / ExtentHeight);

    public override bool ShowsEnd => this.scrollTop >= MaxScrollTop - 0.5;

    // The wheel and the scrollbars - the user moving the view, which abandons a pending top row.
    public override void ScrollByPixels(double delta)
    {
        this.pendingTopRow = null;
        SetVerticalOffset(this.scrollTop + delta);
    }

    public override void ScrollToFraction(double fraction)
    {
        this.pendingTopRow = null;
        SetVerticalOffset(Math.Clamp(fraction, 0, 1) * ExtentHeight);
    }

    public override void ScrollToEnd()
    {
        this.pendingTopRow = null;
        SetVerticalOffset(MaxScrollTop);
    }

    public override double PanViewportWidth => Math.Max(0, Bounds.Width - this.gutterWidth);

    public override double PanStep => Math.Max(1, CharAdvance * 4);

    private void SetVerticalOffset(double y)
    {
        double clamped = Math.Clamp(y, 0, MaxScrollTop);
        if (clamped != this.scrollTop)
        {
            this.scrollTop = clamped;
            InvalidateVisual();
        }

        NotifyScrollPosition();
    }

    private void ScrollRowIntoView(int row)
    {
        double top = row * RowHeight;
        if (top < this.scrollTop)
            SetVerticalOffset(top);
        else if (top + RowHeight > this.scrollTop + BodyHeight)
            SetVerticalOffset(top + RowHeight - BodyHeight);
    }

    protected override void OnViewportChanged()
    {
        SetVerticalOffset(this.scrollTop);
        ApplyPendingTopRow();
    }

    // ---- what changed -------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RowsProperty)
        {
            SubscribeRows();
            DropCells();
            this.pendingTopRow = null;
            SetVerticalOffset(0);
            InvalidateVisual();
        }
        else if (change.Property == StructureProperty)
        {
            OnStructureChanged(change.GetOldValue<TableStructure?>());
        }
        else if (change.Property == LineNumberingProperty || change.Property == WidestGutterLabelProperty)
        {
            MeasureGutter();
        }
        else if (change.Property == ColumnHeadersProperty)
        {
            OnColumnHeadersChanged();
        }
        else if (change.Property == LinkForegroundProperty)
        {
            this.headers.Clear();
        }
    }

    protected override void OnTextStyleChanged()
    {
        this.charAdvance = 0;
        DropCells();
        this.headers.Clear();
        if (this.columns is { } layout && Structure is { } structure)
        {
            layout.Reseed(OpeningWidths(structure));
            OnColumnWidthsChanged();
        }

        MeasureGutter();
    }

    private void SubscribeRows()
    {
        if (this.subscribedRows is not null)
            this.subscribedRows.CollectionChanged -= OnRowsChanged;

        this.subscribedRows = Rows as INotifyCollectionChanged;
        if (this.subscribedRows is not null)
            this.subscribedRows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>The rows grew while indexing, or were replaced (the header tickbox shifts them).</summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            DropCells();

        SetVerticalOffset(this.scrollTop);
        ApplyPendingTopRow();
        InvalidateVisual();
    }

    /// <summary>
    /// A new structure. The same columns relabelled - the header tickbox - keep every width,
    /// including the ones the user set; different columns start again from their discovered
    /// widths.
    /// </summary>
    private void OnStructureChanged(TableStructure? previous)
    {
        this.headers.Clear();
        if (Structure is not { } structure)
        {
            this.columns = null;
            DropCells();
            OnColumnWidthsChanged();
            return;
        }

        if (this.columns is null || previous is null || !SameShape(previous, structure))
        {
            this.columns = new TableColumnLayout(OpeningWidths(structure));
            DropCells();
            OnColumnWidthsChanged();
        }

        InvalidateVisual();
    }

    private static bool SameShape(TableStructure a, TableStructure b)
    {
        if (a.ColumnCount != b.ColumnCount)
            return false;

        for (int c = 0; c < a.ColumnCount; c++)
        {
            if (a.Columns[c].MaxChars != b.Columns[c].MaxChars)
                return false;
        }

        return true;
    }

    /// <summary>Widths go with the pan range and every laid-out cell.</summary>
    private void OnColumnWidthsChanged()
    {
        DropCells();
        this.headers.Clear();
        ResetWidestRowWidth(this.columns?.TotalWidth ?? 0);
        InvalidateVisual();
    }

    private void DropCells()
    {
        this.cells.Clear();
        this.cachedRows = (0, -1);
    }

    // ---- measuring ----------------------------------------------------------------------

    /// <summary>The widest character of the content font, which a character count is widthed by
    /// - on a proportional fallback, the widest wins, so a column errs wide rather than trimming.</summary>
    private double CharAdvance
    {
        get
        {
            if (this.charAdvance > 0)
                return this.charAdvance;

            var typeface = new Typeface(FontFamily);
            foreach (char character in WidestCharacters)
            {
                var sample = new TextLayout(new string(character, 32), typeface, FontSize, Brushes.Black);
                this.charAdvance = Math.Max(this.charAdvance, sample.WidthIncludingTrailingWhitespace / 32);
            }

            return this.charAdvance = Math.Max(1, this.charAdvance);
        }
    }

    /// <summary>The width a column needs for <paramref name="chars"/> characters.</summary>
    public double WidthForChars(int chars) => chars * CharAdvance + 2 * CellPaddingX;

    private double[] OpeningWidths(TableStructure structure)
    {
        var widths = new double[structure.ColumnCount];
        for (int c = 0; c < widths.Length; c++)
            widths[c] = WidthForChars(structure.Columns[c].MaxChars);
        return widths;
    }

    private void MeasureGutter()
    {
        double width = 0;
        if (WidestGutterLabel is { } widest)
        {
            var layout = new TextLayout(widest, new Typeface(FontFamily), FontSize, Brushes.Black);
            width = Math.Max(MinGutterWidth, layout.WidthIncludingTrailingWhitespace + GutterGap + ContentPaddingX);
        }

        if (width == this.gutterWidth)
            return;

        this.gutterWidth = width;
        ResetWidestRowWidth(this.columns?.TotalWidth ?? 0);
        InvalidateVisual();
    }

    /// <summary>Fits <paramref name="column"/> to its widest value among the rows already read, and
    /// its name - never a scan of the file for one double-click. Each is measured as drawn, since
    /// a character count widthed by the widest character overshoots in a proportional font; a
    /// value too short to beat the widest so far is not laid out at all.</summary>
    private void FitColumn(int column)
    {
        if (this.columns is not { } layout || Structure is not { } structure)
            return;

        var typeface = new Typeface(FontFamily);
        string header = PiecedHeader(column) is { } pieced ? string.Concat(PieceTexts(pieced)) : structure.Columns[column].Name;
        double widest = MeasuredWidth(header, new Typeface(FontFamily, weight: FontWeight.SemiBold));
        if (FitSource is { } source)
        {
            foreach (string value in source.RealizedTexts(column))
            {
                // Twice the widest Latin character covers a wide CJK or emoji one.
                if (value.Length * 2 * CharAdvance > widest)
                    widest = Math.Max(widest, MeasuredWidth(value, typeface));
            }
        }

        if (widest == 0)
            return;

        layout.SetUserWidth(column, Math.Ceiling(widest) + 2 * CellPaddingX, MinColumnWidth);
        OnColumnWidthsChanged();
    }

    /// <summary>Test seam: the width fitting a column to <paramref name="value"/> alone gives it.</summary>
    internal double MeasuredFitWidth(string value) => Math.Ceiling(MeasuredWidth(value, new Typeface(FontFamily))) + 2 * CellPaddingX;

    /// <summary>How wide a value is drawn on one line, padding aside.</summary>
    private double MeasuredWidth(string value, Typeface typeface)
        => value.Length == 0 ? 0 : new TextLayout(ControlGlyphs.ForDisplay(value), typeface, FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;

    // ---- drawing ------------------------------------------------------------------------

    private readonly record struct CellText(TextLayout Layout, string Text, bool Trimmed);

    protected override void RenderRows(DrawingContext context)
    {
        if (this.columns is not { } layout || Structure is not { } structure)
            return;

        var typeface = new Typeface(FontFamily);
        var foreground = Foreground ?? Brushes.Black;
        if (GridLineBrush is { } lineBrush && this.gridPen?.Brush != lineBrush)
            this.gridPen = new Pen(lineBrush, 1);

        double width = Bounds.Width;
        double cellsLeft = this.gutterWidth;
        var (firstColumn, lastColumn) = layout.Covering(PanOffset, PanOffset + PanViewportWidth);
        var (firstRow, lastRow) = RowsOnScreen();
        PruneCells(firstRow, lastRow);
        var hoveredCell = HoveredClickableCell();

        // Body.
        using (context.PushClip(new Rect(0, HeaderHeight, width, BodyHeight)))
        {
            for (int r = firstRow; r <= lastRow; r++)
            {
                if (Rows![r] is not TableRow row)
                    continue;

                double y = HeaderHeight + r * RowHeight - this.scrollTop;
                if (r % 2 == 1 && RowBandBrush is { } band)
                    context.FillRectangle(band, new Rect(0, y, width, RowHeight));
                if (r == SelectedIndex && SelectionBrush is { } selection)
                    context.FillRectangle(selection, new Rect(0, y, width, RowHeight));

                if (WidestGutterLabel is not null)
                    DrawGutterLabel(context, row, typeface, y);

                using (context.PushClip(new Rect(cellsLeft, y, Math.Max(0, width - cellsLeft), RowHeight)))
                {
                    for (int c = firstColumn; c <= lastColumn && c < row.Cells.Count; c++)
                    {
                        var cell = CellAt(r, c, row.Cells[c].Text, layout.Width(c), typeface, foreground);
                        double x = cellsLeft + layout.Left(c) - PanOffset;
                        int? current = CurrentMatch is { } m && m.Row == r && m.Column == c ? m.Occurrence : null;
                        bool hovered = hoveredCell == (r, c);
                        using (context.PushClip(new Rect(x, y, layout.Width(c), RowHeight)))
                        {
                            if (hovered && HoverBrush is { } tint)
                                context.FillRectangle(tint, new Rect(x, y, layout.Width(c), RowHeight));
                            DrawText(context, cell, x + CellPaddingX, y, current: current);
                            if (hovered)
                                DrawOpenMark(context, x + layout.Width(c), y);
                        }
                    }
                }
            }

            DrawColumnLines(context, layout, firstColumn, lastColumn, HeaderHeight, Bounds.Height);
        }

        DrawHeader(context, structure, layout, firstColumn, lastColumn, typeface, foreground);

        if (this.gridPen is { } pen)
        {
            context.DrawLine(pen, new Point(0, HeaderHeight - 0.5), new Point(width, HeaderHeight - 0.5));
            if (this.gutterWidth > 0)
                context.DrawLine(pen, new Point(this.gutterWidth - 0.5, 0), new Point(this.gutterWidth - 0.5, Bounds.Height));
        }
    }

    /// <summary>Lets go of the cells of rows that have scrolled away, so the cache stays a
    /// screen's worth.</summary>
    private void PruneCells(int firstRow, int lastRow)
    {
        if (this.cachedRows == (firstRow, lastRow))
            return;

        this.cachedRows = (firstRow, lastRow);
        this.pruning.Clear();
        foreach (var key in this.cells.Keys)
        {
            if (key.Row < firstRow || key.Row > lastRow)
                this.pruning.Add(key);
        }

        foreach (var key in this.pruning)
            this.cells.Remove(key);
    }

    private CellText CellAt(int row, int column, string value, double columnWidth, Typeface typeface, IBrush foreground)
    {
        if (this.cells.TryGetValue((row, column), out var cached))
            return cached;

        var cell = LayOut(value, columnWidth, typeface, FontWeight.Normal, foreground);
        this.cells[(row, column)] = cell;
        return cell;
    }

    /// <summary>A value on one line, cut with an ellipsis where it outruns its column.</summary>
    private CellText LayOut(string value, double columnWidth, Typeface typeface, FontWeight weight, IBrush foreground)
    {
        string text = ControlGlyphs.ForDisplay(value);
        var layout = new TextLayout(text, new Typeface(typeface.FontFamily, weight: weight), FontSize, foreground,
            TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis,
            maxWidth: Math.Max(0, columnWidth - 2 * CellPaddingX));

        bool trimmed = false;
        foreach (var line in layout.TextLines)
            trimmed |= line.HasCollapsed;

        return new CellText(layout, text, trimmed);
    }

    /// <summary>
    /// Draws a cell or column name, with a pill behind each occurrence of the find term - and the
    /// occurrence find is on, <paramref name="current"/>, marked as every view marks it.
    /// </summary>
    private void DrawText(DrawingContext context, CellText cell, double x, double rowTop, double rowHeight = RowHeight, int? current = null)
    {
        double top = rowTop + Math.Max(0, (rowHeight - cell.Layout.Height) / 2);
        double pillTop = rowTop + (rowHeight - RowHeight) / 2;
        var segments = SearchTextSplitter.Split(cell.Text, HighlightTerm);
        Rect? currentText = null;
        string currentMatch = string.Empty;

        if (segments is not null)
        {
            int occurrence = 0;
            foreach (var segment in segments)
            {
                if (!segment.IsMatch)
                    continue;

                bool isCurrent = occurrence++ == current;
                foreach (var hit in cell.Layout.HitTestTextRange(segment.Start, segment.Length))
                {
                    var rect = new Rect(x + hit.X, top + hit.Y, hit.Width, hit.Height);
                    if (isCurrent)
                    {
                        currentText = rect;
                        currentMatch = cell.Text.Substring(segment.Start, segment.Length);
                        DrawCurrentMatchPill(context, rect, pillTop, currentMatch, overText: false);
                    }
                    else
                    {
                        DrawMatchPill(context, rect, pillTop);
                    }
                }
            }
        }

        cell.Layout.Draw(context, new Point(x, top));

        if (currentText is { } text)
            DrawCurrentMatchPill(context, text, pillTop, currentMatch, overText: true);
    }

    private void DrawGutterLabel(DrawingContext context, TableRow row, Typeface typeface, double y)
    {
        var label = new TextLayout(LineNumbering.GutterLabel(row.LineNumber, row.LineStart), typeface, FontSize,
            GutterForeground ?? Foreground ?? Brushes.Gray);
        double x = this.gutterWidth - GutterGap / 2 - label.WidthIncludingTrailingWhitespace;
        label.Draw(context, new Point(x, CentreInRow(label, y)));
    }

    private void DrawColumnLines(DrawingContext context, TableColumnLayout layout, int first, int last, double top, double bottom)
    {
        if (this.gridPen is not { } pen || last < first)
            return;

        using (context.PushClip(new Rect(this.gutterWidth, 0, Math.Max(0, Bounds.Width - this.gutterWidth), Bounds.Height)))
        {
            for (int c = first; c <= last; c++)
            {
                double x = this.gutterWidth + layout.Right(c) - PanOffset - 0.5;
                context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            }
        }
    }

    private void DrawHeader(DrawingContext context, TableStructure structure, TableColumnLayout layout, int first, int last,
        Typeface typeface, IBrush foreground)
    {
        if (HeaderBackground is { } background)
            context.FillRectangle(background, new Rect(0, 0, Bounds.Width, HeaderHeight));

        using (context.PushClip(new Rect(this.gutterWidth, 0, Math.Max(0, Bounds.Width - this.gutterWidth), HeaderHeight)))
        {
            for (int c = first; c <= last; c++)
            {
                var name = HeaderAt(c, structure, layout, typeface, foreground);
                double x = this.gutterWidth + layout.Left(c) - PanOffset;
                using (context.PushClip(new Rect(x, 0, layout.Width(c), HeaderHeight)))
                {
                    DrawText(context, name, x + CellPaddingX, 0, HeaderHeight);
                    DrawLinkUnderlines(context, c, name, x + CellPaddingX);
                }
            }
        }

        DrawColumnLines(context, layout, first, last, 0, HeaderHeight);
    }

    // ---- pointer and keys ---------------------------------------------------------------

    /// <summary>The column whose right edge a header point is on, or -1.</summary>
    private int EdgeAt(Point point)
        => point.Y < HeaderHeight && point.X >= this.gutterWidth && this.columns is { } layout
            ? layout.EdgeNear(point.X - this.gutterWidth + PanOffset, ResizeGrip)
            : -1;

    /// <summary>The row and column a body point is in, or null.</summary>
    private (int Row, int Column)? CellAtPoint(Point point)
    {
        if (point.Y < HeaderHeight || point.X < this.gutterWidth || this.columns is not { } layout)
            return null;

        int row = (int)((point.Y - HeaderHeight + this.scrollTop) / RowHeight);
        int column = layout.ColumnAt(point.X - this.gutterWidth + PanOffset);
        return row < RowCount && column >= 0 ? (row, column) : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        this.pendingTopRow = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();
        var point = e.GetPosition(this);

        int edge = EdgeAt(point);
        if (edge >= 0)
        {
            if (e.ClickCount == 2)
            {
                FitColumn(edge);
            }
            else
            {
                this.resizingColumn = edge;
                this.resizeStartX = point.X;
                this.resizeStartWidth = this.columns!.Width(edge);
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            return;
        }

        if (point.Y < HeaderHeight)
        {
            e.Handled = RaiseClick(point, e.ClickCount);
            return;
        }

        int row = (int)((point.Y - HeaderHeight + this.scrollTop) / RowHeight);
        if (row < RowCount)
            SelectedIndex = row;
        e.Handled = true;
        RaiseClick(point, e.ClickCount);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (this.resizingColumn >= 0 && this.columns is { } layout)
        {
            layout.SetUserWidth(this.resizingColumn, this.resizeStartWidth + point.X - this.resizeStartX, MinColumnWidth);
            OnColumnWidthsChanged();
            return;
        }

        TrackPointer(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (this.resizingColumn >= 0)
        {
            this.resizingColumn = -1;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        this.resizingColumn = -1;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        TrackPointer(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        this.pendingTopRow = null;
        int count = RowCount;
        if (e.Handled || count == 0)
            return;

        int page = Math.Max(1, (int)(BodyHeight / RowHeight) - 1);
        int current = SelectedIndex;
        int? next = e.Key switch
        {
            Key.Down => current + 1,
            Key.Up => current < 0 ? 0 : current - 1,
            Key.PageDown => current + page,
            Key.PageUp => current - page,
            Key.Home => 0,
            Key.End => count - 1,
            _ => null,
        };

        if (next is { } row)
        {
            SelectedIndex = Math.Clamp(row, 0, count - 1);
            ScrollRowIntoView(SelectedIndex);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            RequestPan(Math.Max(0, PanOffset + (e.Key == Key.Right ? PanStep : -PanStep)));
            e.Handled = true;
        }
    }
}
