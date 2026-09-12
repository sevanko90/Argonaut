using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Draws the raw viewer's rows itself, rather than templating one control per row.
///
/// The raw view used to be a <c>ListBox</c> over a virtualizing panel, which is an excellent way
/// to show rows and a poor foundation for a caret: selection means whole rows, the caret's pixel
/// position needs a text layout the row template owns, and moving a caret between rows means
/// coordinating dozens of recycled item containers around one piece of state. Drawing every
/// visible row here puts the text, the selection, the search highlight and the caret in a single
/// coordinate space and a single <see cref="Render"/> pass, so caret movement is one
/// <c>InvalidateVisual</c> and nothing can drift out of step with anything else.
///
/// Virtualization survives the change intact and is the property most worth protecting: rows are
/// a fixed <see cref="RowHeight"/> and byte-capped, so the visible range is arithmetic on the
/// scroll offset and nothing outside it is ever materialized. <see cref="ILogicalScrollable"/>
/// means the hosting <c>ScrollViewer</c> still supplies the wheel, the scrollbar, page keys and
/// bring-into-view - this supplies the viewport arithmetic, not a scroll engine.
///
/// Horizontal movement is deliberately NOT scrolling: rows are laid out at their natural width
/// and panned by <see cref="PanOffset"/>, which keeps both gutters pinned while the text slides
/// under them. That is the behaviour the ListBox version had, driven by a render transform.
/// </summary>
public class RawTextSurface : Control, ILogicalScrollable
{
    /// <summary>
    /// Row height, in device-independent pixels. Was a hard-coded <c>Height</c> on the ListBoxItem
    /// style in XAML, where code could not see it; the caret's vertical position needs it, so it
    /// lives here now and the view has no say.
    /// </summary>
    public const double RowHeight = 22;

    /// <summary>Width of the line-number gutter, including <see cref="LineNumberGap"/>.</summary>
    public const double LineNumberColumnWidth = 100;

    /// <summary>Space between the right-aligned line number and the text.</summary>
    public const double LineNumberGap = 12;

    /// <summary>Width of the right-hand gutter holding the soft-wrap marker.</summary>
    public const double WrapGutterWidth = 18;

    /// <summary>Padding inside the surface, matching the ListBox padding it replaces.</summary>
    public const double ContentPaddingX = 8;

    private const string WrapMarker = "⏎";

    /// <summary>Caret thickness, in device-independent pixels.</summary>
    private const double CaretWidth = 1.5;

    /// <summary>
    /// Text layouts for the rows currently on screen. Keyed by row index and dropped wholesale
    /// whenever anything that changes how a row is drawn changes, which is cheaper and far easier
    /// to reason about than invalidating individual entries - a viewport is a few dozen rows.
    /// </summary>
    private readonly Dictionary<int, TextLayout> layouts = new();

    /// <summary>
    /// The rows currently on screen, refreshed whenever the viewport or the document moves.
    /// Deliberately NOT computed inside <see cref="Render"/>: virtualization is a property of
    /// layout, not of drawing, and a headless test has no renderer - so deciding what is visible
    /// during the render pass would make the one guarantee most worth testing untestable.
    /// </summary>
    private readonly List<(int Index, RawVisibleRow Row)> realized = new();

    /// <summary>
    /// Decoded rows for the rows on screen, carrying the character-to-byte map the caret and the
    /// selection need. Same lifetime as <see cref="layouts"/>; a viewport's worth at most.
    /// </summary>
    private readonly Dictionary<int, RawDecodedRow> decoded = new();

    private RawViewModel? viewModel;
    private INotifyCollectionChanged? subscribedRows;
    private RawCaretController? caret;
    private DispatcherTimer? caretBlink;
    private bool caretVisible = true;
    private bool dragging;

    /// <summary>
    /// The x the caret is trying to keep while moving up and down, in surface coordinates. Reset
    /// by any horizontal movement, so Up/Down through a short row does not permanently lose the
    /// column the user started in.
    /// </summary>
    private double? stickyX;
    private Vector offset;
    private int? pendingRevealRow;
    private double panOffset;
    private EventHandler? scrollInvalidated;

    /// <summary>Widest row laid out since the last <see cref="DropLayouts"/>, in pixels.</summary>
    private double widestRowWidth;

    /// <summary>The realized range <see cref="widestRowWidth"/> was last measured over.</summary>
    private (int First, int Last) measuredRange = (0, -1);

    static RawTextSurface()
    {
        FocusableProperty.OverrideDefaultValue<RawTextSurface>(true);

        // Transparent rather than null, so the surface is hit-testable even if the brush resource
        // it is bound to fails to resolve. A caret that silently stops responding to clicks
        // because of a missing theme key is not a failure anyone would look for here.
        BackgroundProperty.OverrideDefaultValue<RawTextSurface>(Brushes.Transparent);
        AffectsRender<RawTextSurface>(
            ForegroundProperty,
            GutterForegroundProperty,
            HighlightBrushProperty,
            SelectionBrushProperty,
            CaretBrushProperty,
            BackgroundProperty);
    }

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<RawTextSurface>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<RawTextSurface>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RawTextSurface>();

    /// <summary>Brush for the line-number and wrap-marker gutters.</summary>
    public static readonly StyledProperty<IBrush?> GutterForegroundProperty =
        AvaloniaProperty.Register<RawTextSurface, IBrush?>(nameof(GutterForeground));

    /// <summary>Brush behind occurrences of the find term.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<RawTextSurface, IBrush?>(nameof(HighlightBrush));

    /// <summary>Brush behind selected text.</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<RawTextSurface, IBrush?>(nameof(SelectionBrush));

    /// <summary>Brush for the caret itself.</summary>
    public static readonly StyledProperty<IBrush?> CaretBrushProperty =
        AvaloniaProperty.Register<RawTextSurface, IBrush?>(nameof(CaretBrush));

    /// <summary>
    /// The surface's own background. Not decoration: a control with nothing painted behind it is
    /// not reliably hit-testable, so without this a click passes straight through and the caret
    /// never moves.
    /// </summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<RawTextSurface>();

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? GutterForeground
    {
        get => GetValue(GutterForegroundProperty);
        set => SetValue(GutterForegroundProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public IBrush? CaretBrush
    {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>
    /// How far the text is panned left, in pixels. Not scrolling: the gutters stay put and only
    /// the text column moves, which is what keeps a line number aligned with its row at any
    /// horizontal position.
    /// </summary>
    public double PanOffset
    {
        get => this.panOffset;
        set
        {
            if (Math.Abs(this.panOffset - value) < 0.01)
                return;

            this.panOffset = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Rows the surface currently holds materialized, for tests. A surface that realizes rows
    /// outside its viewport is the runaway-memory failure mode virtualization exists to prevent,
    /// and on a multi-GB file it is not subtle.
    /// </summary>
    internal int RealizedRowCount => this.realized.Count;

    /// <summary>First and last row currently realized, for tests. Empty is (0, -1).</summary>
    internal (int First, int Last) RealizedRowRange { get; private set; } = (0, -1);

    /// <summary>
    /// Text layouts currently held, for tests. The pair of caches here is the only thing in the
    /// surface that could grow with distance travelled rather than with what is on screen, so a
    /// soak test asserts on this directly rather than inferring it from the heap.
    /// </summary>
    internal int CachedLayoutCount => this.layouts.Count;

    /// <summary>
    /// How wide the text column has actually needed to be, in pixels: the widest row measured
    /// since the last time the layouts were dropped. This is what the pan range is sized from.
    ///
    /// It is a high-water mark rather than the widest row on screen, because a range measured
    /// from the current viewport would shrink and grow as the user scrolled, moving the thumb
    /// under their hand. It resets whenever the layouts do - a new document, a new wrap width, a
    /// new font - which is exactly when the old measurement stops meaning anything.
    /// </summary>
    public double WidestRowWidth => this.widestRowWidth;

    /// <summary>Raised when <see cref="WidestRowWidth"/> grows, so the host can resize the pan range.</summary>
    public event EventHandler? WidestRowWidthChanged;

    private int RowCount => this.viewModel?.RowCount ?? 0;

    /// <summary>Left edge of the text column.</summary>
    private double TextOriginX => ContentPaddingX + LineNumberColumnWidth;

    /// <summary>Width available to text before the wrap-marker gutter.</summary>
    private double TextViewportWidth
        => Math.Max(0, Bounds.Width - TextOriginX - WrapGutterWidth - ContentPaddingX);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as RawViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (this.subscribedRows is not null)
        {
            this.subscribedRows.CollectionChanged -= OnRowsChanged;
            this.subscribedRows = null;
        }

        if (this.viewModel is not null)
            this.viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void Attach(RawViewModel? next)
    {
        if (ReferenceEquals(this.viewModel, next))
            return;

        if (this.viewModel is not null)
            this.viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        this.viewModel = next;

        if (this.viewModel is not null)
            this.viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SubscribeRows();
        AttachCaret();
        DropLayouts();
        InvalidateScrollable();
        InvalidateVisual();
    }

    private void AttachCaret()
    {
        if (this.caret is not null)
            this.caret.Moved -= OnCaretMoved;

        this.caret = this.viewModel?.Caret;

        if (this.caret is not null)
            this.caret.Moved += OnCaretMoved;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        // A caret that blinked out mid-movement reads as a dropped keystroke, so any movement
        // restarts the phase solid.
        ShowCaretSolid();
        ScrollCaretIntoView();
        InvalidateVisual();
    }

    /// <summary>
    /// Follows the row set's growth notifications. The row count climbs for the whole of a
    /// background scan without the view model raising a property change for each step - the row
    /// collection reports it instead - so without this the surface would draw whatever was
    /// indexed at the moment it was attached and never learn about the rest.
    /// </summary>
    private void SubscribeRows()
    {
        if (this.subscribedRows is not null)
            this.subscribedRows.CollectionChanged -= OnRowsChanged;

        this.subscribedRows = this.viewModel?.HasRows == true ? this.viewModel.Rows : null;

        if (this.subscribedRows is not null)
            this.subscribedRows.CollectionChanged += OnRowsChanged;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateScrollable();
        InvalidateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null:
            case nameof(RawViewModel.HighlightTerm):
                DropLayouts();
                InvalidateVisual();
                break;

            case nameof(RawViewModel.Caret):
                AttachCaret();
                InvalidateVisual();
                break;

            case nameof(RawViewModel.RowCount):
            case nameof(RawViewModel.Rows):
                SubscribeRows();
                DropLayouts();
                InvalidateScrollable();
                InvalidateVisual();
                break;
        }
    }

    /// <summary>
    /// Drops every cached layout. Called whenever the way a row is drawn changes - the font, the
    /// find term, the document - rather than trying to work out which rows are affected. The
    /// cache only ever holds a viewport's worth, so rebuilding it is a few dozen layouts.
    /// </summary>
    private void DropLayouts()
    {
        this.layouts.Clear();
        this.decoded.Clear();

        // The rows themselves are about to be laid out differently, so what was measured over
        // them means nothing - including which range it was measured over.
        this.measuredRange = (0, -1);

        if (this.widestRowWidth == 0)
            return;

        this.widestRowWidth = 0;
        NotifyWidestRowWidthChanged();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            DropLayouts();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            InvalidateScrollable();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateRealizedRows(finalSize.Height);

        // After the reveal, not before: a reveal scrolls, which realizes a different set of rows,
        // and measuring the set it replaced would report a width for rows nobody is looking at.
        ApplyPendingReveal();
        MeasureRealizedRows();
        return finalSize;
    }

    /// <summary>
    /// Works out which rows the viewport covers and materializes exactly those. The whole of
    /// virtualization is the two lines of arithmetic at the top: every row is exactly
    /// <see cref="RowHeight"/> tall because the index caps rows by byte count, so the visible
    /// range follows from the scroll offset without measuring anything.
    /// </summary>
    private void UpdateRealizedRows(double height)
    {
        this.realized.Clear();

        int rowCount = RowCount;
        if (this.viewModel is null || rowCount == 0 || height <= 0)
        {
            RealizedRowRange = (0, -1);
            this.layouts.Clear();
            return;
        }

        // Ceiling-minus-one rather than a plain truncation: a row whose top sits exactly on the
        // viewport's bottom edge shows nothing at all, and realizing it is a row of work for no
        // pixels.
        int firstRow = Math.Clamp((int)(this.offset.Y / RowHeight), 0, rowCount - 1);
        int lastRow = Math.Clamp((int)Math.Ceiling((this.offset.Y + height) / RowHeight) - 1, firstRow, rowCount - 1);
        RealizedRowRange = (firstRow, lastRow);

        for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
        {
            if (this.viewModel.Rows[rowIndex] is RawVisibleRow row)
                this.realized.Add((rowIndex, row));
        }

        PruneLayouts(firstRow, lastRow);
    }

    /// <summary>
    /// Lays out every realized row and keeps the widest.
    ///
    /// Called from both the layout pass and <see cref="Render"/>, and does nothing unless the
    /// realized range has moved since it last ran. Drawing is where a row's layout is needed
    /// anyway, so measuring there is free - but headless has no renderer, and a width only ever
    /// measured while drawing could not be tested, which is what the layout-pass call is for.
    /// Scroll invalidations are deliberately NOT a call site: a scan in flight raises one per
    /// growth notification, many per second, and after a wrap change (which drops every layout)
    /// each would lay out a fresh viewport of text on the input path.
    /// </summary>
    private void MeasureRealizedRows()
    {
        if (this.realized.Count == 0 || this.measuredRange == RealizedRowRange)
            return;

        this.measuredRange = RealizedRowRange;

        var typeface = new Typeface(FontFamily);
        double fontSize = FontSize;
        var foreground = Foreground ?? Brushes.Black;

        double widest = this.widestRowWidth;
        foreach (var (rowIndex, row) in this.realized)
        {
            var layout = LayoutFor(rowIndex, row, typeface, fontSize, foreground);
            widest = Math.Max(widest, layout.WidthIncludingTrailingWhitespace);
        }

        if (widest <= this.widestRowWidth)
            return;

        this.widestRowWidth = widest;
        NotifyWidestRowWidthChanged();
    }

    /// <summary>
    /// Raises <see cref="WidestRowWidthChanged"/> a dispatcher turn later. One of the call sites
    /// is the render pass, and the host reacts by resizing the pan scrollbar - a visual change,
    /// which Avalonia refuses mid-render ("Visual was invalidated during the render pass"). The
    /// deferral is the same re-entrancy tool the selection setters use (see CLAUDE.md), for the
    /// same reason: decide synchronously, act after the pass that asked has unwound.
    /// </summary>
    private void NotifyWidestRowWidthChanged()
    {
        if (WidestRowWidthChanged is null)
            return;

        UiDeferral.AfterCurrentInput(() => WidestRowWidthChanged?.Invoke(this, EventArgs.Empty));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Background is { } background)
            context.FillRectangle(background, new Rect(Bounds.Size));

        if (this.viewModel is null || this.realized.Count == 0)
            return;

        MeasureRealizedRows();

        var typeface = new Typeface(FontFamily);
        double fontSize = FontSize;
        var foreground = Foreground ?? Brushes.Black;
        var gutter = GutterForeground ?? foreground;

        foreach (var (rowIndex, row) in this.realized)
        {
            double y = rowIndex * RowHeight - this.offset.Y;

            DrawLineNumber(context, row, typeface, fontSize, gutter, y);

            using (context.PushClip(new Rect(TextOriginX, y, TextViewportWidth, RowHeight)))
            {
                var layout = LayoutFor(rowIndex, row, typeface, fontSize, foreground);

                // Everything that belongs to the text - the glyphs, the selection behind them,
                // the highlight, the caret - is drawn against the same centred band, so none of
                // them can sit at a different height from the others.
                double textTop = CentreInRow(layout, y);

                DrawSelection(context, layout, row, rowIndex, textTop);
                DrawHighlights(context, layout, rowIndex, textTop);
                layout.Draw(context, new Point(TextOriginX - this.panOffset, textTop));
                DrawCaret(context, layout, rowIndex, textTop);
            }

            if (row.IsSoftWrapped)
                DrawWrapMarker(context, typeface, fontSize, gutter, y);
        }
    }

    /// <summary>
    /// Top of a line of text within its row band. Rows are a fixed 22px while the text is however
    /// tall the content font makes it, so the difference is split above and below rather than
    /// left at the bottom - which is what the ListBox item template used to do for free.
    /// </summary>
    private static double CentreInRow(TextLayout layout, double rowTop)
        => rowTop + Math.Max(0, (RowHeight - layout.Height) / 2);

    private void DrawLineNumber(DrawingContext context, RawVisibleRow row, Typeface typeface, double fontSize, IBrush brush, double y)
    {
        // Continuation rows leave the gutter blank, which is how a wrapped line reads as one line.
        if (row.LineNumber is not int lineNumber)
            return;

        var layout = new TextLayout(lineNumber.ToString(), typeface, fontSize, brush);
        double x = ContentPaddingX + LineNumberColumnWidth - LineNumberGap - layout.WidthIncludingTrailingWhitespace;
        layout.Draw(context, new Point(x, CentreInRow(layout, y)));
    }

    private void DrawWrapMarker(DrawingContext context, Typeface typeface, double fontSize, IBrush brush, double y)
    {
        var layout = new TextLayout(WrapMarker, typeface, fontSize, brush);
        double x = Bounds.Width - ContentPaddingX - WrapGutterWidth
                   + (WrapGutterWidth - layout.WidthIncludingTrailingWhitespace) / 2;
        layout.Draw(context, new Point(x, CentreInRow(layout, y)));
    }

    /// <summary>
    /// Paints a background behind every occurrence of the find term. Matching is re-done against
    /// the row's displayed text rather than mapped from the search's byte offsets: the row is
    /// decoded and substituted, so re-finding the term in what is actually on screen lights up
    /// every visible occurrence rather than only the one the search is sitting on.
    /// </summary>
    private void DrawHighlights(DrawingContext context, TextLayout layout, int rowIndex, double y)
    {
        if (HighlightBrush is not { } brush)
            return;

        var origin = new Vector(TextOriginX - this.panOffset, y);
        foreach (var rect in HighlightRectsFor(rowIndex, layout))
            context.FillRectangle(brush, rect.Translate(origin));
    }

    /// <summary>
    /// The highlight rectangles for one row, in layout coordinates.
    ///
    /// A match that straddles a soft-wrap boundary is the case worth describing. The row is only
    /// part of its line, so half the term sits here and half on the row below - and searching
    /// either row's text alone finds neither half. So the term is looked for in a window that
    /// reaches one term-length into the neighbouring rows, and each row paints whatever part of a
    /// match falls inside it. Both halves light up, in their own rows.
    ///
    /// The reach only crosses soft-wrap boundaries, never a real line ending: a line break is a
    /// place a match genuinely cannot span, and reaching across one would highlight text that
    /// merely happens to adjoin.
    /// </summary>
    internal IReadOnlyList<Rect> HighlightRectsFor(int rowIndex, TextLayout layout)
    {
        string? term = this.viewModel?.HighlightTerm;
        if (string.IsNullOrEmpty(term) || RowTextAt(rowIndex) is not { } text)
            return Array.Empty<Rect>();

        // One less than the term: any more cannot contribute to a match overlapping this row.
        int reach = term.Length - 1;
        string prefix = reach > 0 && ContinuesInto(rowIndex) ? Tail(RowTextAt(rowIndex - 1), reach) : string.Empty;
        string suffix = reach > 0 && IsSoftWrapped(rowIndex) ? Head(RowTextAt(rowIndex + 1), reach) : string.Empty;

        var segments = SearchTextSplitter.Split(prefix + text + suffix, term);
        if (segments is null)
            return Array.Empty<Rect>();

        List<Rect>? rects = null;
        foreach (var segment in segments)
        {
            if (!segment.IsMatch)
                continue;

            // Back into this row's own coordinates, then clipped to it - a match reaching in from
            // a neighbour paints only the part that is actually here.
            int start = Math.Max(segment.Start - prefix.Length, 0);
            int end = Math.Min(segment.Start - prefix.Length + segment.Length, text.Length);
            if (end <= start)
                continue;

            rects ??= new List<Rect>();
            rects.AddRange(layout.HitTestTextRange(start, end - start));
        }

        return (IReadOnlyList<Rect>?)rects ?? Array.Empty<Rect>();
    }

    private string? RowTextAt(int rowIndex)
        => this.viewModel is { } vm && vm.HasRows && (uint)rowIndex < (uint)vm.RowCount
            ? (vm.Rows[rowIndex] as RawVisibleRow)?.Text
            : null;

    /// <summary>True when the row was force-broken, so its line continues on the next row.</summary>
    private bool IsSoftWrapped(int rowIndex)
        => this.viewModel is { } vm && vm.HasRows && (uint)rowIndex < (uint)vm.RowCount
           && (vm.Rows[rowIndex] as RawVisibleRow)?.IsSoftWrapped == true;

    /// <summary>True when the previous row was force-broken, so this row continues its line.</summary>
    private bool ContinuesInto(int rowIndex) => rowIndex > 0 && IsSoftWrapped(rowIndex - 1);

    private static string Tail(string? text, int count)
        => string.IsNullOrEmpty(text) ? string.Empty : text[Math.Max(0, text.Length - count)..];

    private static string Head(string? text, int count)
        => string.IsNullOrEmpty(text) ? string.Empty : text[..Math.Min(count, text.Length)];

    private TextLayout LayoutFor(int rowIndex, RawVisibleRow row, Typeface typeface, double fontSize, IBrush foreground)
        => LayoutFor(rowIndex, typeface, fontSize, foreground, row.Text);

    private TextLayout LayoutFor(int rowIndex, Typeface typeface, double fontSize, IBrush foreground, string text)
    {
        if (this.layouts.TryGetValue(rowIndex, out var cached))
            return cached;

        // No wrapping and no width constraint: the row is already byte-capped by the index, and
        // laying it out at its natural width is what lets it be panned rather than re-flowed.
        var layout = new TextLayout(text, typeface, fontSize, foreground, TextAlignment.Left, TextWrapping.NoWrap);
        this.layouts[rowIndex] = layout;
        return layout;
    }

    private void PruneLayouts(int firstRow, int lastRow)
    {
        if (this.layouts.Count <= (lastRow - firstRow + 1) * 2)
            return;

        var stale = new List<int>();
        foreach (int rowIndex in this.layouts.Keys)
        {
            if (rowIndex < firstRow || rowIndex > lastRow)
                stale.Add(rowIndex);
        }

        foreach (int rowIndex in stale)
        {
            this.layouts.Remove(rowIndex);
            this.decoded.Remove(rowIndex);
        }
    }

    // ---- caret and selection ------------------------------------------------------------

    /// <summary>Asks the host to pan so a given surface x is visible. The pan scrollbar belongs
    /// to the view, so the surface requests rather than sets.</summary>
    public event EventHandler<double>? PanRequested;

    private IByteSource? Source => this.viewModel?.Bytes;

    private IRawRowIndex? RowIndex => this.viewModel?.Index;

    /// <summary>
    /// Which row the caret draws on. At a soft-wrap boundary one offset belongs to two rows, and
    /// the affinity is the only thing that says which - the whole reason a caret carries one.
    /// </summary>
    private int? CaretRowIndex()
    {
        if (this.caret is null || RowIndex is not { } index || index.RowCount == 0)
            return null;

        long offset = this.caret.Caret.Offset;
        int rowIndex = index.RowForOffset(offset) ?? index.RowCount - 1;

        if (this.caret.Caret.Affinity == CaretAffinity.Upstream
            && rowIndex > 0
            && index.GetRowInfo(rowIndex).Start == offset)
        {
            rowIndex--;
        }

        return rowIndex;
    }

    private RawDecodedRow? DecodedRow(int rowIndex)
    {
        if (Source is not { } source || RowIndex is not { } index)
            return null;

        if (this.decoded.TryGetValue(rowIndex, out var cached))
            return cached;

        if ((uint)rowIndex >= (uint)index.RowCount)
            return null;

        var info = index.GetRowInfo(rowIndex);
        var row = RawRowDecoder.Decode(source, info.Start, info.End, info.IsSoftWrapped);
        this.decoded[rowIndex] = row;
        return row;
    }

    /// <summary>Surface x of a byte offset inside a row, before panning.</summary>
    private double XForOffset(int rowIndex, TextLayout layout, long offset)
    {
        if (DecodedRow(rowIndex) is not { } row)
            return 0;

        int charIndex = row.CharIndexForByte((int)(offset - row.RowStart));
        return layout.HitTestTextPosition(charIndex).X;
    }

    private void DrawSelection(DrawingContext context, TextLayout layout, RawVisibleRow row, int rowIndex, double y)
    {
        if (this.caret is null || SelectionBrush is not { } brush)
            return;

        var selection = this.caret.Selection;
        if (selection.IsEmpty || DecodedRow(rowIndex) is not { } decodedRow)
            return;

        // A row's selectable extent is its DRAWN text; the newline bytes at the end are inside
        // the row's range but are not on screen, so selecting through them must not paint past
        // the last glyph.
        long drawnEnd = row.Start + decodedRow.DisplayByteLength;
        if (selection.Intersect(row.Start, drawnEnd) is not var (start, end))
            return;

        int startChar = decodedRow.CharIndexForByte((int)(start - row.Start));
        int endChar = decodedRow.CharIndexForByte((int)(end - row.Start));
        if (endChar <= startChar)
            return;

        var origin = new Vector(TextOriginX - this.panOffset, y);
        foreach (var rect in layout.HitTestTextRange(startChar, endChar - startChar))
            context.FillRectangle(brush, rect.Translate(origin));
    }

    private void DrawCaret(DrawingContext context, TextLayout layout, int rowIndex, double textTop)
    {
        if (!this.caretVisible || CaretBrush is not { } brush)
            return;

        if (CaretRectFor(rowIndex, layout, textTop) is { } rect)
            context.FillRectangle(brush, rect);
    }

    /// <summary>
    /// Where the caret is drawn on this row, or null when the caret is not on it. As tall as the
    /// text rather than as tall as the row: a caret spanning the full row height overhangs the
    /// glyphs by the row's leading and reads as too long.
    /// </summary>
    private Rect? CaretRectFor(int rowIndex, TextLayout layout, double textTop)
    {
        if (this.caret is null || CaretRowIndex() != rowIndex)
            return null;

        double x = TextOriginX - this.panOffset + XForOffset(rowIndex, layout, this.caret.Caret.Offset);
        return new Rect(Math.Floor(x), textTop, CaretWidth, layout.Height);
    }

    /// <summary>
    /// The caret's rectangle in surface coordinates, for tests. Headless has no renderer, so the
    /// geometry has to be reachable without drawing - and caret geometry is exactly the kind of
    /// thing that looks fine in code and wrong on screen.
    /// </summary>
    internal Rect? CaretRect()
    {
        if (this.caret is null || CaretRowIndex() is not int rowIndex || DecodedRow(rowIndex) is not { } row)
            return null;

        var typeface = new Typeface(FontFamily);
        var layout = LayoutFor(rowIndex, typeface, FontSize, Foreground ?? Brushes.Black, row.Text);
        double y = rowIndex * RowHeight - this.offset.Y;
        return CaretRectFor(rowIndex, layout, CentreInRow(layout, y));
    }

    /// <summary>Top of the row band the caret sits in, for tests.</summary>
    internal double? CaretRowTop()
        => CaretRowIndex() is int rowIndex ? rowIndex * RowHeight - this.offset.Y : null;

    /// <summary>Byte offset under a point, for click and drag.</summary>
    private long? OffsetAt(Point point)
    {
        if (RowIndex is not { } index || index.RowCount == 0)
            return null;

        int rowIndex = Math.Clamp((int)((point.Y + this.offset.Y) / RowHeight), 0, index.RowCount - 1);
        if (DecodedRow(rowIndex) is not { } row)
            return null;

        var info = index.GetRowInfo(rowIndex);

        var typeface = new Typeface(FontFamily);
        var layout = LayoutFor(rowIndex, typeface, FontSize, Foreground ?? Brushes.Black, row.Text);

        double x = point.X - TextOriginX + this.panOffset;
        var hit = layout.HitTestPoint(new Point(Math.Max(0, x), 0));

        // Clicking past the end of the glyphs means the end of the row's text, not the end of its
        // byte range - the newline is not a place the caret can be.
        int charIndex = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        charIndex = Math.Clamp(charIndex, 0, row.Text.Length);
        return info.Start + row.ByteOffsetForChar(charIndex);
    }

    private void ShowCaretSolid()
    {
        this.caretVisible = true;
        this.caretBlink?.Stop();
        this.caretBlink?.Start();
    }

    private void ScrollCaretIntoView()
    {
        // A reveal in flight owns the viewport. Without this the caret's minimal scroll would
        // pre-empt a centred reveal that is still waiting for its row to be indexed.
        if (this.pendingRevealRow is not null)
            return;

        if (CaretRowIndex() is not int rowIndex)
            return;

        ScrollRowIntoView(rowIndex);

        if (this.caret is null || DecodedRow(rowIndex) is not { } row)
            return;

        var typeface = new Typeface(FontFamily);
        var layout = LayoutFor(rowIndex, typeface, FontSize, Foreground ?? Brushes.Black, row.Text);
        double x = XForOffset(rowIndex, layout, this.caret.Caret.Offset);

        double visibleLeft = this.panOffset;
        double visibleRight = this.panOffset + TextViewportWidth;
        if (x < visibleLeft)
            PanRequested?.Invoke(this, Math.Max(0, x - TextViewportWidth / 4));
        else if (x > visibleRight)
            PanRequested?.Invoke(this, x - TextViewportWidth + TextViewportWidth / 4);
    }

    /// <summary>
    /// Up and down, which live here rather than in <see cref="RawCaretController"/> because
    /// "the same column, one row up" is a pixel question: the content font may be proportional,
    /// so the column is an x and only the text layouts know where an x falls.
    /// </summary>
    private void MoveVertical(int rowDelta, bool extend)
    {
        if (this.caret is null || RowIndex is not { } index || index.RowCount == 0)
            return;

        if (CaretRowIndex() is not int rowIndex)
            return;

        var typeface = new Typeface(FontFamily);
        var foreground = Foreground ?? Brushes.Black;

        if (this.stickyX is not double targetX)
        {
            if (DecodedRow(rowIndex) is not { } current)
                return;

            var currentLayout = LayoutFor(rowIndex, typeface, FontSize, foreground, current.Text);
            targetX = XForOffset(rowIndex, currentLayout, this.caret.Caret.Offset);
            this.stickyX = targetX;
        }

        int destinationRow = Math.Clamp(rowIndex + rowDelta, 0, index.RowCount - 1);
        if (destinationRow == rowIndex)
            return;

        if (DecodedRow(destinationRow) is not { } destination)
            return;

        var destinationLayout = LayoutFor(destinationRow, typeface, FontSize, foreground, destination.Text);
        var hit = destinationLayout.HitTestPoint(new Point(Math.Max(0, targetX), 0));
        int charIndex = Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, destination.Text.Length);

        var info = index.GetRowInfo(destinationRow);
        long offset = info.Start + destination.ByteOffsetForChar(charIndex);

        // Downstream keeps the caret on the row it moved to when that row starts at a wrap
        // boundary shared with the row above.
        double keptX = targetX;
        this.caret.MoveTo(offset, extend, CaretAffinity.Downstream);
        this.stickyX = keptX;
    }

    // ---- input --------------------------------------------------------------------------

    /// <summary>
    /// The platform's "command" modifier. Both are accepted rather than switching on the OS, so
    /// Ctrl keeps working on a Mac keyboard and Cmd on a Mac - which is what people actually hit.
    /// </summary>
    private static bool HasCommandModifier(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        AbandonPendingReveal();

        if (this.caret is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();

        if (OffsetAt(e.GetPosition(this)) is not long offset)
            return;

        if (e.ClickCount == 2)
        {
            SelectWordAt(offset);
            e.Handled = true;
            return;
        }

        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (extend)
            this.caret.ExtendTo(offset);
        else
            this.caret.PlaceAt(offset);

        this.stickyX = null;
        this.dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>
    /// Double-click word selection. The caret is already sitting where the first click of the
    /// pair put it, so a refusal needs no repair - it only needs saying, because a double-click
    /// that silently does nothing reads as a dead surface.
    /// </summary>
    internal void SelectWordAt(long offset)
    {
        if (this.caret is null)
            return;

        this.stickyX = null;

        if (!this.caret.SelectWordAt(offset))
            ToastService.Show("Word too long for selection");
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!this.dragging || this.caret is null)
            return;

        var position = e.GetPosition(this);

        // Dragging above or below the surface scrolls, so a selection can run past the viewport.
        if (position.Y < 0)
            ScrollRowIntoView(Math.Max(0, RealizedRowRange.First - 1));
        else if (position.Y > Bounds.Height)
            ScrollRowIntoView(Math.Min(RowCount - 1, RealizedRowRange.Last + 1));

        if (OffsetAt(position) is long offset)
            this.caret.ExtendTo(offset);

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!this.dragging)
            return;

        this.dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        AbandonPendingReveal();

        if (this.caret is null || e.Handled)
            return;

        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool command = HasCommandModifier(e.KeyModifiers);

        switch (e.Key)
        {
            case Key.Left:
                this.caret.MoveLeft(extend);
                this.stickyX = null;
                break;

            case Key.Right:
                this.caret.MoveRight(extend);
                this.stickyX = null;
                break;

            case Key.Up:
                MoveVertical(-1, extend);
                break;

            case Key.Down:
                MoveVertical(1, extend);
                break;

            case Key.PageUp:
                MoveVertical(-VisibleRowCount(), extend);
                break;

            case Key.PageDown:
                MoveVertical(VisibleRowCount(), extend);
                break;

            case Key.Home:
                if (command)
                    this.caret.MoveToDocumentStart(extend);
                else
                    this.caret.MoveToRowStart(extend);
                this.stickyX = null;
                break;

            case Key.End:
                if (command)
                    this.caret.MoveToDocumentEnd(extend);
                else
                    this.caret.MoveToRowEnd(extend);
                this.stickyX = null;
                break;

            case Key.A when command:
                this.caret.SelectAll();
                this.stickyX = null;
                break;

            case Key.C when command:
                _ = CopySelectionAsync();
                break;

            default:
                return; // not ours; leave it for the window's own handlers
        }

        e.Handled = true;
    }

    private int VisibleRowCount() => Math.Max(1, (int)(Bounds.Height / RowHeight) - 1);

    /// <summary>
    /// Copies the selection. Refuses rather than truncates when the selection is larger than
    /// <see cref="RawTextExtractor.MaxExtractBytes"/> - selecting a whole multi-GB document is
    /// free and perfectly reasonable, turning it into a string is not.
    /// </summary>
    private async Task CopySelectionAsync()
    {
        if (this.caret is null || Source is not { } source)
            return;

        var selection = this.caret.Selection;
        if (selection.IsEmpty)
            return;

        if (!RawTextExtractor.TryExtract(source, selection.Start, selection.End, out string text))
        {
            ToastService.Show($"Selection is too large to copy ({selection.Length / (1024 * 1024)} MB).");
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(text);
        ToastService.Show($"Copied {selection.Length:N0} {(selection.Length == 1 ? "byte" : "bytes")} to clipboard");
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        StartBlink();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        StopBlink();
    }

    private void StartBlink()
    {
        this.caretVisible = true;
        this.caretBlink ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(530),
            DispatcherPriority.Background,
            (_, _) =>
            {
                this.caretVisible = !this.caretVisible;
                InvalidateVisual();
            });

        this.caretBlink.Start();
        InvalidateVisual();
    }

    private void StopBlink()
    {
        this.caretBlink?.Stop();
        this.caretVisible = false;
        InvalidateVisual();
    }

    // ---- ILogicalScrollable -------------------------------------------------------------
    //
    // Vertical only. Horizontal movement is PanOffset, driven by the view's own pan scrollbar,
    // because scrolling horizontally would take the gutters with it.

    /// <summary>
    /// Computed live rather than cached. A cached extent goes stale between the row count
    /// growing and whatever refreshes it running, and the host clamps any offset it is given
    /// against that stale value - which silently turns a reveal deep in a large file into a
    /// scroll that stops short. During a full-speed scan one 120ms growth tick is over a million
    /// rows of staleness, so "stale by one tick" is not a rounding error, it is a mile.
    /// </summary>
    public Size Extent => new(Bounds.Width, Math.Max(RowCount * RowHeight, Bounds.Height));

    public Size Viewport => Bounds.Size;

    public Vector Offset
    {
        get => this.offset;
        set
        {
            if (this.offset == value)
                return;

            this.offset = value;
            UpdateRealizedRows(Bounds.Height);
            InvalidateVisual();
        }
    }

    public bool CanHorizontallyScroll
    {
        get => false;
        set { }
    }

    public bool CanVerticallyScroll
    {
        get => true;
        set { }
    }

    public bool IsLogicalScrollEnabled => true;

    /// <summary>One wheel notch, and the arrow-key step the ScrollViewer applies.</summary>
    public Size ScrollSize => new(1, RowHeight);

    public Size PageScrollSize => new(Viewport.Width, Math.Max(RowHeight, Viewport.Height - RowHeight));

    public event EventHandler? ScrollInvalidated
    {
        add => this.scrollInvalidated += value;
        remove => this.scrollInvalidated -= value;
    }

    public void RaiseScrollInvalidated(EventArgs e) => this.scrollInvalidated?.Invoke(this, e);

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    /// <summary>Tells the host the extent moved, and re-tries a reveal that is still waiting.</summary>
    private void InvalidateScrollable()
    {
        UpdateRealizedRows(Bounds.Height);
        RaiseScrollInvalidated(EventArgs.Empty);
        ApplyPendingReveal();
    }

    /// <summary>
    /// Brings <paramref name="rowIndex"/> just inside the viewport, moving as little as possible.
    /// What caret movement uses: arrowing off the bottom edge should advance by a row, not leap.
    /// </summary>
    public void ScrollRowIntoView(int rowIndex)
    {
        if (Bounds.Height <= 0 || (uint)rowIndex >= (uint)RowCount)
            return;

        double top = rowIndex * RowHeight;
        double bottom = top + RowHeight;

        if (top < this.offset.Y)
            SetVerticalOffset(top);
        else if (bottom > this.offset.Y + Bounds.Height)
            SetVerticalOffset(bottom - Bounds.Height);
    }

    /// <summary>
    /// Reveals <paramref name="rowIndex"/> centred in the viewport - what a jump to a parse
    /// failure or a search hit uses. Centring rather than scrolling minimally is the difference
    /// between landing the target on the last line of the window, where it has no following
    /// context and the eye has to hunt for it, and landing it where the eye already is with
    /// context either side.
    ///
    /// A row already fully on screen is left alone: re-centring something the user can already
    /// see would move the view for no reason.
    ///
    /// Remembered rather than applied once, because a reveal can be asked for while the scan is
    /// still short of that row, and the host can clamp the offset against an extent that has not
    /// caught up. Any input from the user abandons it - being yanked back to a search hit after
    /// scrolling away would be worse than the reveal never landing.
    /// </summary>
    public void RevealRow(int rowIndex)
    {
        this.pendingRevealRow = rowIndex;
        ApplyPendingReveal();
    }

    private void ApplyPendingReveal()
    {
        if (this.pendingRevealRow is not int rowIndex)
            return;

        if (Bounds.Height <= 0 || rowIndex >= RowCount)
            return; // the scan has not reached it yet; a later growth tick will re-try

        double top = rowIndex * RowHeight;
        if (top >= this.offset.Y && top + RowHeight <= this.offset.Y + Bounds.Height)
        {
            this.pendingRevealRow = null; // already on screen
            return;
        }

        SetVerticalOffset(top - ((Bounds.Height - RowHeight) / 2));

        // Settled only once the row is genuinely realized. If the host clamped the offset short,
        // this stays pending and the next growth tick tries again against a larger extent.
        if (RealizedRowRange.First <= rowIndex && rowIndex <= RealizedRowRange.Last)
            this.pendingRevealRow = null;
    }

    private void SetVerticalOffset(double y)
    {
        double limit = Math.Max(0, Extent.Height - Bounds.Height);
        Offset = new Vector(this.offset.X, Math.Clamp(y, 0, limit));
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    /// <summary>A reveal in flight belongs to the app, not the user; their first input ends it.</summary>
    private void AbandonPendingReveal() => this.pendingRevealRow = null;
}
