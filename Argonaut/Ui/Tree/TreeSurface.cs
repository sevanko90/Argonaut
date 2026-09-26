using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Ui.Find;
using Argonaut.Ui.Rows;

namespace Argonaut.Ui.Tree;

/// <summary>
/// Draws a tree of any format - JSON today, XML later - from a <see cref="TreeDocument"/>, one
/// fixed-height row at a time, with nothing materialised beyond the rows on screen. The format
/// supplies what a row says (<see cref="ITreeRowPainter"/>) and any gutters; indentation, expand
/// arrows, selection, find highlights, keyboard and pointer handling are here and shared.
///
/// <b>Scrolling is anchored, not indexed.</b> There is no row count and no row numbering: the
/// surface holds a cursor on its top row (the anchor) and walks from it to fill the viewport. Its
/// position is a fraction of the document - the anchor's byte position - so a scrollbar's thumb
/// is where the view is in the file. The wheel, a trackpad, arrows and pages move the anchor by
/// rows (<see cref="ScrollByPixels"/>); dragging a thumb seeks to the byte it points at
/// (<see cref="ScrollToFraction"/>). The scrollbar is the host's, and only ever follows
/// (<see cref="ScrollPositionChanged"/>) - it never feeds a correction back, which is what would
/// make a dragged thumb stutter.
/// </summary>
public class TreeSurface : RowSurface
{
    /// <summary>Horizontal step per nesting level.</summary>
    public const double IndentWidth = 16;

    /// <summary>Width of the expand-arrow column before each row's text.</summary>
    public const double ToggleWidth = 16;

    /// <summary>
    /// The expand arrows, as geometry rather than text: a ▸/▾ glyph picks up font-fallback metrics
    /// that differ by platform (and by glyph within one fallback chain on Windows), so the two
    /// states drew at visibly different sizes. A path is the same size everywhere.
    /// </summary>
    private const double ArrowSize = 7;

    private static readonly Geometry CollapsedArrowShape = Geometry.Parse("M 0,0 L 7,3.5 L 0,7 Z");
    private static readonly Geometry ExpandedArrowShape = Geometry.Parse("M 0,0 L 7,0 L 3.5,7 Z");

    /// <summary>Width of the slot a row's marker (see <see cref="ITreeRowPainter.Marker"/>) takes
    /// before its arrow, including the gap after it.</summary>
    public const double MarkerWidth = 28;

    /// <summary>How close to a resizable gutter's edge the pointer has to be to grab it.</summary>
    private const double ResizeGrip = 4;

    /// <summary>Rows an Alt-expand may reveal before it stops: enough to open any sensible
    /// subtree, few enough that one click near the root of a huge file stays quick.</summary>
    public int DeepExpandRowBudget { get; set; } = 100_000;

    /// <summary>A row covers this many bytes until rows on screen say otherwise.</summary>
    private const double InitialBytesPerRow = 32;

    /// <summary>One row as laid out: its text, the marker before its arrow, and where its links
    /// are in the text.</summary>
    private sealed record RowLayout(TextLayout Layout, string Text, TextLayout? Marker, List<(int Start, int Length, object Link)>? Links);

    private readonly List<TreeRow> realized = new();
    private readonly Dictionary<(long, TreeRowShape, bool), RowLayout> layouts = new();
    private readonly List<TreeRun> runs = new();
    private IResizableTreeGutter? resizing;
    private double resizeStartX;
    private double resizeStartWidth;
    private object? toolTipShown;
    private DispatcherTimer? toolTipDelay;

    private TreeDocument? document;
    private TreeCursor? anchor;
    private double anchorPixel;
    private double bytesPerRow = InitialBytesPerRow;
    private TreeCursor? selection;
    private string? highlightTerm;
    private IReadOnlyDictionary<TreeRunStyle, IBrush>? runBrushes;

    /// <summary>The document shown. Setting it resets the view to the top.</summary>
    public TreeDocument? Document
    {
        get => document;
        set
        {
            if (ReferenceEquals(document, value))
                return;

            if (document is not null)
            {
                document.Grew -= OnDocumentGrew;
                document.Closing -= OnDocumentClosing;
            }

            document = value;
            if (document is not null)
            {
                document.Grew += OnDocumentGrew;
                document.Closing += OnDocumentClosing;
            }

            ResetToTop();
        }
    }

    /// <summary>The find term to highlight in rows on screen, or null.</summary>
    public string? HighlightTerm
    {
        get => highlightTerm;
        set
        {
            highlightTerm = value;
            InvalidateVisual();
        }
    }

    /// <summary>The brush per run style; a style with none uses <see cref="RowSurface.Foreground"/>.</summary>
    public IReadOnlyDictionary<TreeRunStyle, IBrush>? RunBrushes
    {
        get => runBrushes;
        set
        {
            runBrushes = value;
            DropLayouts();
            InvalidateVisual();
        }
    }

    /// <summary>The selected row, or null.</summary>
    public TreeRow? SelectedRow => selection?.Current;

    /// <summary>The selected row's text as the painter gives it, or null.</summary>
    public string? SelectedRowText => selection is { } cursor ? RowText(cursor.Current) : null;

    public event EventHandler? SelectionChanged;

    /// <summary>A link run was clicked: the row, and the run's <see cref="TreeRun.Link"/>.</summary>
    public event EventHandler<TreeLinkClickedEventArgs>? LinkClicked;

    /// <summary>An Alt-expand stopped at <see cref="DeepExpandRowBudget"/> rows.</summary>
    public event EventHandler? ExpandLimitReached;

    /// <summary>Behind the gutters, so they read as a panel beside the tree.</summary>
    public static readonly StyledProperty<IBrush?> GutterBackgroundProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(GutterBackground));

    /// <summary>The line between the gutters and the tree.</summary>
    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(DividerBrush));

    public IBrush? GutterBackground
    {
        get => GetValue(GutterBackgroundProperty);
        set => SetValue(GutterBackgroundProperty, value);
    }

    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    static TreeSurface()
    {
        AffectsRender<TreeSurface>(GutterBackgroundProperty, DividerBrushProperty);
    }

    /// <summary>A gutter changed width or what it shows - a schema bound or unbound.</summary>
    public void InvalidateGutters()
    {
        InvalidateVisual();
    }

    /// <summary>A container was expanded or collapsed from the surface.</summary>
    public event EventHandler? ExpansionChanged;

    /// <summary>The rows on screen, top first, for tests. Only ever a viewport's worth.</summary>
    internal IReadOnlyList<TreeRow> RealizedRows => realized;

    /// <summary>Text layouts held, for tests: bounded by the viewport, not by distance scrolled.</summary>
    internal int CachedLayoutCount => layouts.Count;

    /// <summary>Where the first link on realized row <paramref name="index"/> is drawn, in surface
    /// coordinates, for tests; null when it has none.</summary>
    internal Rect? LinkBounds(int index)
    {
        var row = realized[index];
        var laid = LayoutFor(row, new Typeface(FontFamily), FontSize);
        if (laid.Links is not { Count: > 0 } links)
            return null;

        double textX = ArrowLeft(row, laid) + ToggleWidth;
        double textTop = CentreInRow(laid.Layout, index * RowHeight - anchorPixel);
        foreach (var rect in laid.Layout.HitTestTextRange(links[0].Start, links[0].Length))
            return rect.Translate(new Vector(textX, textTop));

        return null;
    }

    /// <summary>Pixels of the top row scrolled off the top.</summary>
    internal double AnchorPixel => anchorPixel;

    private double GutterWidth
    {
        get
        {
            double width = 0;
            if (document is not null)
            {
                foreach (var gutter in document.Gutters)
                    width += gutter.Width;
            }

            return width;
        }
    }

    private double ContentLeft
    {
        get
        {
            double gutters = GutterWidth;
            return gutters > 0 ? ContentPaddingX + gutters + ContentPaddingX : ContentPaddingX;
        }
    }

    /// <summary>How wide the rows' own area is - the surface less its gutters and padding - which
    /// is what a host's pan range compares <see cref="RowSurface.WidestRowWidth"/> against.</summary>
    public double ContentViewportWidth => Math.Max(0, Bounds.Width - ContentLeft - ContentPaddingX);

    /// <summary>Where a row's arrow sits, panned: after its indent and any marker.</summary>
    private double ArrowLeft(in TreeRow row, RowLayout layout)
        => ContentLeft + row.Depth * IndentWidth + (layout.Marker is null ? 0 : MarkerWidth) - PanOffset;

    // ---- navigation the view model drives -------------------------------------------------

    /// <summary>
    /// Selects the row showing <paramref name="offset"/> and brings it to the middle of the view,
    /// expanding the containers that hide it when <paramref name="expandAncestors"/> - what a
    /// search hit or a path jump does.
    /// </summary>
    public void Reveal(long offset, bool expandAncestors)
    {
        if (document is null)
            return;

        var cursor = document.NewCursor();
        if (!cursor.SeekTo(offset))
            return;

        // A collapsed container that holds the offset past its opening hides it: open it and
        // look again, one level at a time.
        while (expandAncestors && cursor.Current is { Shape: TreeRowShape.Open, IsExpanded: false } hidden
               && offset >= document.Reader.FirstChildPosition(hidden.Node.ValueStart)
               && offset < ContainerEndOrMax(hidden.Node.ValueStart))
        {
            document.Expand.SetExpanded(hidden.Node.ValueStart, hidden.Depth, true);
            cursor.SeekTo(offset);
        }

        selection = cursor;
        anchor = cursor.Clone();
        anchorPixel = 0;
        for (int above = VisibleRowCount() / 2; above > 0 && anchor.MovePrevious(); above--)
        {
        }

        DropLayouts();
        Realize();
        NotifyScrollPosition();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Expands or collapses the container of <paramref name="row"/>.</summary>
    public void Toggle(in TreeRow row)
    {
        if (document is null || row.Shape == TreeRowShape.Leaf)
            return;

        document.Expand.Toggle(row.Node.ValueStart);
        Reseat();
        ExpansionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What rows say has changed - a schema bound, a hint setting - though the rows
    /// themselves have not: lay them out again.</summary>
    public void InvalidateRows()
    {
        DropLayouts();
        MeasureRealizedRows();
        InvalidateVisual();
    }

    /// <summary>Re-reads what is on screen after the expansion changed elsewhere - a new default
    /// depth, say. The top row and the selection stay put, or move to the container now hiding
    /// them.</summary>
    public void Reseat()
    {
        if (anchor is not null)
            anchor.SeekTo(anchor.Current.Start);
        if (selection is not null)
            selection.SeekTo(selection.Current.Start);

        Realize();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    private long ContainerEndOrMax(long containerStart)
    {
        var index = document!.Index;
        int record = index.FindContainerStartingAt(containerStart);
        return record >= 0 && index.GetContainer(record).End is var end and >= 0
            ? end
            : document.Reader.SkipValue(containerStart);
    }

    // ---- rows on screen ---------------------------------------------------------------------

    private void ResetToTop()
    {
        anchor = document?.NewCursor();
        if (anchor is not null && !anchor.MoveToStart())
            anchor = null;

        anchorPixel = 0;
        selection = null;
        bytesPerRow = InitialBytesPerRow;
        DropLayouts();
        ResetWidestRowWidth();
        Realize();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    private void OnDocumentClosing(object? sender, EventArgs e) => Document = null;

    private void OnDocumentGrew(object? sender, EventArgs e)
    {
        if (anchor is null)
        {
            ResetToTop();
            return;
        }

        Realize();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    /// <summary>
    /// Walks from the anchor to fill the viewport. At the end of the document the anchor is
    /// pulled back so the last row sits at the bottom rather than the view running out under a
    /// half-empty screen.
    /// </summary>
    private void Realize()
    {
        realized.Clear();
        ShowsEnd = false;
        if (anchor is null || Bounds.Height <= 0)
            return;

        int needed = (int)Math.Ceiling((Bounds.Height + anchorPixel) / RowHeight);
        var walker = anchor.Clone();
        realized.Add(walker.Current);
        bool more = true;
        while (realized.Count < needed && (more = walker.MoveNext()))
            realized.Add(walker.Current);

        // One more step finds out whether the last row realized is the document's last.
        if (more && realized.Count == needed)
            more = walker.MoveNext();
        ShowsEnd = !more && realized.Count * RowHeight - anchorPixel <= Bounds.Height + 0.5;

        int fitting = Math.Max(1, (int)(Bounds.Height / RowHeight));
        if (realized.Count < needed && realized.Count < fitting)
        {
            anchorPixel = 0;
            while (realized.Count < fitting && anchor.MovePrevious())
                realized.Insert(0, anchor.Current);
        }

        EstimateBytesPerRow();
        PruneLayouts();
        MeasureRealizedRows();
    }

    /// <summary>Refines the bytes a row covers from the rows on screen, smoothed so the scroll
    /// range does not lurch with every screen.</summary>
    private void EstimateBytesPerRow()
    {
        if (realized.Count < 2)
            return;

        long span = realized[^1].Start - realized[0].Start;
        if (span <= 0)
            return;

        double measured = (double)span / (realized.Count - 1);
        bytesPerRow = Math.Max(1, bytesPerRow * 0.75 + measured * 0.25);
    }

    private int VisibleRowCount() => Math.Max(1, (int)(Bounds.Height / RowHeight));

    private void MeasureRealizedRows()
    {
        var typeface = new Typeface(FontFamily);
        double widest = WidestRowWidth;
        foreach (var row in realized)
        {
            var laid = LayoutFor(row, typeface, FontSize);
            double marker = laid.Marker is null ? 0 : MarkerWidth;
            widest = Math.Max(widest, row.Depth * IndentWidth + marker + ToggleWidth + laid.Layout.WidthIncludingTrailingWhitespace);
        }

        RecordRowWidth(widest);
    }

    private RowLayout LayoutFor(in TreeRow row, Typeface typeface, double fontSize)
    {
        var key = (row.Node.ValueStart, row.Shape, row.IsExpanded);
        if (layouts.TryGetValue(key, out var cached))
            return cached;

        var foreground = Foreground ?? Brushes.Black;
        runs.Clear();
        document!.Painter.AppendRuns(row, runs);

        var text = new StringBuilder();
        var overrides = new List<ValueSpan<TextRunProperties>>(runs.Count);
        List<(int, int, object)>? links = null;
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
                continue;

            var brush = runBrushes is not null && runBrushes.TryGetValue(run.Style, out var styled) ? styled : foreground;
            overrides.Add(new ValueSpan<TextRunProperties>(text.Length, run.Text.Length,
                new GenericTextRunProperties(typeface, fontSize, foregroundBrush: brush)));
            if (run.Link is { } link)
                (links ??= new()).Add((text.Length, run.Text.Length, link));
            text.Append(run.Text);
        }

        string content = text.ToString();
        var layout = new TextLayout(content, typeface, fontSize, foreground, TextAlignment.Left, TextWrapping.NoWrap,
            textStyleOverrides: overrides);

        TextLayout? marker = null;
        if (document.Painter.Marker(row) is { } label)
        {
            var markerBrush = runBrushes is not null && runBrushes.TryGetValue(TreeRunStyle.Hint, out var muted) ? muted : foreground;
            marker = new TextLayout(label, typeface, Math.Max(6, fontSize * 0.75), markerBrush);
        }

        var entry = new RowLayout(layout, content, marker, links);
        layouts[key] = entry;
        return entry;
    }

    private string RowText(in TreeRow row)
    {
        runs.Clear();
        document?.Painter.AppendRuns(row, runs);
        var text = new StringBuilder();
        foreach (var run in runs)
            text.Append(run.Text);
        return text.ToString();
    }

    /// <summary>Keeps the layout cache to about the rows on screen; a viewport's worth of
    /// layouts is cheap to rebuild, and a cache that grew with distance scrolled would not
    /// be.</summary>
    private void PruneLayouts()
    {
        if (layouts.Count <= realized.Count * 2)
            return;

        var keep = new HashSet<(long, TreeRowShape, bool)>();
        foreach (var row in realized)
            keep.Add((row.Node.ValueStart, row.Shape, row.IsExpanded));

        var stale = new List<(long, TreeRowShape, bool)>();
        foreach (var key in layouts.Keys)
        {
            if (!keep.Contains(key))
                stale.Add(key);
        }

        foreach (var key in stale)
            layouts.Remove(key);
    }

    private void DropLayouts()
    {
        layouts.Clear();
    }

    protected override void OnTextStyleChanged()
    {
        DropLayouts();
        ResetWidestRowWidth();
        MeasureRealizedRows();
    }

    protected override void OnViewportChanged()
    {
        Realize();
        NotifyScrollPosition();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Realize();
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (document is null || realized.Count == 0)
            return;

        var typeface = new Typeface(FontFamily);
        double fontSize = FontSize;
        var foreground = Foreground ?? Brushes.Black;
        var gutterStyle = new TreeGutterStyle(typeface, fontSize, GutterForeground ?? foreground);
        var selectedKey = selection?.Current.Key;
        double contentLeft = ContentLeft;
        double contentWidth = Math.Max(0, Bounds.Width - contentLeft - ContentPaddingX);


        double gutterWidth = GutterWidth;
        if (gutterWidth > 0)
        {
            if (GutterBackground is { } panel)
                context.FillRectangle(panel, new Rect(0, 0, ContentPaddingX + gutterWidth, Bounds.Height));
            if (DividerBrush is { } divider)
                context.FillRectangle(divider, new Rect(ContentPaddingX + gutterWidth, 0, 1, Bounds.Height));
        }

        for (int i = 0; i < realized.Count; i++)
        {
            var row = realized[i];
            double y = i * RowHeight - anchorPixel;

            if (SelectionBrush is { } selectionBrush && row.Key == selectedKey)
                context.FillRectangle(selectionBrush, new Rect(contentLeft, y, Math.Max(0, Bounds.Width - contentLeft), RowHeight));

            double gutterX = ContentPaddingX;
            foreach (var gutter in document.Gutters)
            {
                if (gutter.Width <= 0)
                    continue;

                using (context.PushClip(new Rect(gutterX, y, gutter.Width, RowHeight)))
                    gutter.Draw(context, row, new Rect(gutterX, y, gutter.Width, RowHeight), gutterStyle);
                gutterX += gutter.Width;
            }

            using (context.PushClip(new Rect(contentLeft, y, contentWidth, RowHeight)))
            {
                var laid = LayoutFor(row, typeface, fontSize);
                double arrowX = ArrowLeft(row, laid);

                if (laid.Marker is { } marker)
                    marker.Draw(context, new Point(arrowX - 2 - marker.WidthIncludingTrailingWhitespace, CentreInRow(marker, y)));

                if (row.Shape == TreeRowShape.Open)
                {
                    var shape = row.IsExpanded ? ExpandedArrowShape : CollapsedArrowShape;
                    var at = Matrix.CreateTranslation(arrowX + (ToggleWidth - ArrowSize) / 2, y + (RowHeight - ArrowSize) / 2);
                    using (context.PushTransform(at))
                        context.DrawGeometry(foreground, null, shape);
                }

                double textTop = CentreInRow(laid.Layout, y);
                double textX = arrowX + ToggleWidth;
                DrawHighlights(context, laid.Layout, laid.Text, textX, textTop);
                laid.Layout.Draw(context, new Point(textX, textTop));
            }
        }
    }

    private void DrawHighlights(DrawingContext context, TextLayout layout, string text, double x, double y)
    {
        if (HighlightBrush is not { } brush || SearchTextSplitter.Split(text, highlightTerm) is not { } segments)
            return;

        foreach (var segment in segments)
        {
            if (!segment.IsMatch)
                continue;

            foreach (var rect in layout.HitTestTextRange(segment.Start, segment.Length))
                context.FillRectangle(brush, rect.Translate(new Vector(x, y)));
        }
    }

    // ---- scrolling ------------------------------------------------------------------------

    /// <summary>
    /// Not hosted in a <c>ScrollViewer</c>: the vertical position is the anchor, and the view's own
    /// scrollbar reads <see cref="ScrollFraction"/> and drives <see cref="ScrollToFraction"/> and
    /// <see cref="ScrollByPixels"/>. A <c>ScrollViewer</c> shares one offset between the host and
    /// the surface, and an estimated extent makes those two disagree - which a dragged thumb feels
    /// as the surface pulling it back under the pointer. So the scroll interface reports no
    /// vertical range at all.
    /// </summary>
    protected override double ExtentHeight => Bounds.Height;

    protected override void OnOffsetChanged()
    {
    }

    /// <summary>Raised whenever the top row moves - a scroll, a reveal, a keyboard move, the
    /// document growing - so a scrollbar can follow.</summary>
    public event EventHandler? ScrollPositionChanged;

    /// <summary>Where the top of the view is in the document, from 0 to 1: the anchor's byte
    /// position, plus the part of a row scrolled off the top.</summary>
    public double ScrollFraction
    {
        get
        {
            long length = document?.AvailableLength ?? 0;
            if (anchor is null || length <= 0)
                return 0;

            double position = anchor.Current.Start + anchorPixel / RowHeight * bytesPerRow;
            return Math.Clamp(position / length, 0, 1);
        }
    }

    /// <summary>How much of the document a screen shows, from 0 to 1, from the average bytes a
    /// row covers - smoothed, so a scrollbar thumb sized from it does not flicker.</summary>
    public double ViewportFraction
    {
        get
        {
            long length = document?.AvailableLength ?? 0;
            return length <= 0 ? 1 : Math.Clamp(VisibleRowCount() * bytesPerRow / length, 0, 1);
        }
    }

    /// <summary>True when the document's last row is on screen in full - the view is at the end,
    /// whatever the byte estimate says.</summary>
    public bool ShowsEnd { get; private set; }

    /// <summary>Shows the end of the document, its last row at the bottom - or, while the index
    /// is still being built, the furthest it has reached.</summary>
    public void ScrollToEnd()
    {
        if (anchor is null || document is null)
            return;

        if (!document.Index.IsComplete)
        {
            ScrollToFraction(1);
            return;
        }

        anchor.MoveToEnd();
        anchorPixel = 0;
        Realize();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    /// <summary>
    /// Puts the byte at <paramref name="fraction"/> of the document at the top - what a dragged
    /// thumb does. At the end, the last row settles at the bottom.
    ///
    /// Never past what the index covers. Beyond it a seek has no resume point nearer than the
    /// last one indexed, and would read every sibling in between - gigabytes of them, on the UI
    /// thread, for each move of the thumb. Until the index gets there, the view stops at its edge.
    /// </summary>
    public void ScrollToFraction(double fraction)
    {
        if (anchor is null || document is null)
            return;

        long target = (long)(Math.Clamp(fraction, 0, 1) * document.AvailableLength);
        if (!document.Index.IsComplete)
            target = Math.Min(target, document.Index.ScannedTo);

        if (target <= 0)
            anchor.MoveToStart();
        else
            anchor.SeekTo(target);

        anchorPixel = 0;
        Realize();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    /// <summary>Moves the view by <paramref name="delta"/> pixels of rows - the wheel, a
    /// trackpad, a scrollbar's arrows and pages. Positive moves down the document.</summary>
    public void ScrollByPixels(double delta)
    {
        if (anchor is null)
            return;

        ScrollBy(delta);
        NotifyScrollPosition();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (anchor is null)
            return;

        // One row per unit of wheel delta, the step a ScrollViewer takes with ScrollSize; a
        // trackpad reports fractions of that, which is what makes it smooth.
        if (e.Delta.Y != 0)
            ScrollByPixels(-e.Delta.Y * RowHeight);
        if (e.Delta.X != 0)
            RequestPan(Math.Max(0, PanOffset - e.Delta.X * RowHeight));

        e.Handled = true;
    }

    /// <summary>Moves the anchor by <paramref name="delta"/> pixels of rows.</summary>
    private void ScrollBy(double delta)
    {
        double total = anchorPixel + delta;
        int rows = (int)Math.Floor(total / RowHeight);
        anchorPixel = total - rows * RowHeight;

        for (; rows > 0; rows--)
        {
            if (!anchor!.MoveNext())
            {
                anchorPixel = 0;
                break;
            }
        }

        for (; rows < 0; rows++)
        {
            if (!anchor!.MovePrevious())
            {
                anchorPixel = 0;
                break;
            }
        }

        Realize();
    }

    private void NotifyScrollPosition() => ScrollPositionChanged?.Invoke(this, EventArgs.Empty);

    // ---- selection and input --------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (document is null)
            return;

        var point = e.GetPosition(this);
        if (GutterEdgeAt(point.X) is { } edge)
        {
            resizing = edge;
            resizeStartX = point.X;
            resizeStartWidth = edge.Width;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (realized.Count == 0)
            return;

        Focus();
        int index = (int)Math.Floor((point.Y + anchorPixel) / RowHeight);
        if ((uint)index >= (uint)realized.Count)
            return;

        var row = realized[index];
        var laid = LayoutFor(row, new Typeface(FontFamily), FontSize);
        double arrowX = ArrowLeft(row, laid);

        if (LinkAt(laid, point.X - arrowX - ToggleWidth, point.Y - CentreInRow(laid.Layout, index * RowHeight - anchorPixel)) is { } link)
        {
            Select(row);
            LinkClicked?.Invoke(this, new TreeLinkClickedEventArgs(row, link));
            e.Handled = true;
            return;
        }

        Select(row);

        bool onArrow = point.X >= arrowX && point.X < arrowX + ToggleWidth;
        if (row.Shape == TreeRowShape.Open && (onArrow || e.ClickCount == 2))
        {
            if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
                ToggleDeep(row);
            else
                Toggle(row);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (resizing is { } gutter)
        {
            gutter.Resize(Math.Max(gutter.MinWidth, resizeStartWidth + point.X - resizeStartX));
            InvalidateVisual();
            return;
        }

        bool overEdge = GutterEdgeAt(point.X) is not null;
        bool overLink = !overEdge && RowAt(point.Y) is { } hovered
            && LayoutFor(hovered.Row, new Typeface(FontFamily), FontSize) is var laid
            && LinkAt(laid, point.X - ArrowLeft(hovered.Row, laid) - ToggleWidth,
                point.Y - CentreInRow(laid.Layout, hovered.Index * RowHeight - anchorPixel)) is not null;
        Cursor = overEdge ? new Cursor(StandardCursorType.SizeWestEast)
            : overLink ? new Cursor(StandardCursorType.Hand)
            : null;

        UpdateToolTip(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (resizing is null)
            return;

        resizing = null;
        e.Pointer.Capture(null);
        DropLayouts();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetToolTip(null);
    }

    private (TreeRow Row, int Index)? RowAt(double y)
    {
        int index = (int)Math.Floor((y + anchorPixel) / RowHeight);
        return (uint)index < (uint)realized.Count ? (realized[index], index) : null;
    }

    /// <summary>The resizable gutter whose right edge is under <paramref name="x"/>, if any.</summary>
    private IResizableTreeGutter? GutterEdgeAt(double x)
    {
        if (document is null)
            return null;

        double edge = ContentPaddingX;
        foreach (var gutter in document.Gutters)
        {
            if (gutter.Width <= 0)
                continue;

            edge += gutter.Width;
            if (gutter is IResizableTreeGutter resizable && Math.Abs(x - edge) <= ResizeGrip)
                return resizable;
        }

        return null;
    }

    /// <summary>The link under a point given in the row text's own coordinates.</summary>
    private static object? LinkAt(RowLayout laid, double x, double y)
    {
        if (laid.Links is null || x < 0 || x > laid.Layout.WidthIncludingTrailingWhitespace)
            return null;

        int position = laid.Layout.HitTestPoint(new Point(x, Math.Clamp(y, 0, laid.Layout.Height - 1))).TextPosition;
        foreach (var (start, length, link) in laid.Links)
        {
            if (position >= start && position < start + length)
                return link;
        }

        return null;
    }

    /// <summary>Shows the tooltip of the gutter cell under the pointer, or none.</summary>
    private void UpdateToolTip(Point point)
    {
        object? tip = null;
        if (document is not null && RowAt(point.Y) is { } hovered)
        {
            double left = ContentPaddingX;
            foreach (var gutter in document.Gutters)
            {
                if (gutter.Width <= 0)
                    continue;

                if (point.X >= left && point.X < left + gutter.Width)
                {
                    tip = gutter.ToolTipFor(hovered.Row);
                    break;
                }

                left += gutter.Width;
            }
        }

        SetToolTip(tip);
    }

    /// <summary>
    /// Puts <paramref name="tip"/> up for the cell under the pointer. Avalonia opens a tooltip only
    /// when the pointer enters its control, and every gutter cell is this one control - so moving
    /// between cells would close the old tooltip and never open the new one. The surface opens it
    /// itself: at once when one was already showing, the way moving between tooltips feels
    /// anywhere else, and otherwise after the usual hover delay.
    /// </summary>
    private void SetToolTip(object? tip)
    {
        if (Equals(tip, toolTipShown))
            return;

        bool wasOpen = ToolTip.GetIsOpen(this);
        toolTipShown = tip;
        toolTipDelay?.Stop();
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, tip);
        if (tip is null)
            return;

        if (wasOpen)
        {
            ToolTip.SetIsOpen(this, true);
            return;
        }

        toolTipDelay ??= new DispatcherTimer(TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(this)), DispatcherPriority.Normal, (_, _) =>
        {
            toolTipDelay!.Stop();
            if (toolTipShown is not null && IsPointerOver)
                ToolTip.SetIsOpen(this, true);
        });
        toolTipDelay.Start();
    }

    /// <summary>
    /// Alt-toggle. A collapsed container opens along with everything beneath it, stopping after
    /// <see cref="DeepExpandRowBudget"/> rows; an expanded one closes and forgets what was opened
    /// beneath it, so opening it again shows the default.
    /// </summary>
    public void ToggleDeep(in TreeRow row)
    {
        if (document is null || row.Shape == TreeRowShape.Leaf)
            return;

        long start = row.Node.ValueStart;
        if (row.IsExpanded || row.Shape == TreeRowShape.Close)
        {
            document.Expand.SetExpanded(start, row.Depth, false);
            document.Expand.ResetWithin(start, ContainerEndOrMax(start));
        }
        else
        {
            // Walk the subtree as if everything were open, opening each container for real.
            var everything = new TreeCursor(document.Index, document.Reader, new TreeExpandState(int.MaxValue));
            everything.SeekTo(row.Start);
            document.Expand.SetExpanded(start, row.Depth, true);

            int budget = DeepExpandRowBudget;
            while (budget-- > 0 && everything.MoveNext() && everything.Current.Depth > row.Depth)
            {
                if (everything.Current is { Shape: TreeRowShape.Open } open)
                    document.Expand.SetExpanded(open.Node.ValueStart, open.Depth, true);
            }

            if (budget < 0)
                ExpandLimitReached?.Invoke(this, EventArgs.Empty);
        }

        Reseat();
        ExpansionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Select(in TreeRow row)
    {
        var cursor = document!.NewCursor();
        cursor.SeekTo(row.Start);
        selection = cursor;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (document is null || anchor is null || e.Handled)
            return;

        if (selection is null)
        {
            if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            {
                selection = anchor.Clone();
                AfterSelectionMoved(downward: false);
                e.Handled = true;
            }

            return;
        }

        var current = selection.Current;
        switch (e.Key)
        {
            case Key.Down:
                if (selection.MoveNext())
                    AfterSelectionMoved(downward: true);
                break;
            case Key.Up:
                if (selection.MovePrevious())
                    AfterSelectionMoved(downward: false);
                break;
            case Key.PageDown:
                for (int i = VisibleRowCount() - 1; i > 0 && selection.MoveNext(); i--)
                {
                }

                AfterSelectionMoved(downward: true);
                break;
            case Key.PageUp:
                for (int i = VisibleRowCount() - 1; i > 0 && selection.MovePrevious(); i--)
                {
                }

                AfterSelectionMoved(downward: false);
                break;
            case Key.Home:
                selection.MoveToStart();
                AfterSelectionMoved(downward: false);
                break;
            case Key.End:
                selection.MoveToEnd();
                AfterSelectionMoved(downward: true);
                break;
            case Key.Right when current is { Shape: TreeRowShape.Open, IsExpanded: false }:
                if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
                    ToggleDeep(current);
                else
                    Toggle(current);
                break;
            case Key.Right when current is { Shape: TreeRowShape.Open }:
                if (selection.MoveNext())
                    AfterSelectionMoved(downward: true);
                break;
            case Key.Left when current is { Shape: TreeRowShape.Open, IsExpanded: true }:
                if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
                    ToggleDeep(current);
                else
                    Toggle(current);
                break;
            case Key.Left:
                SelectParent();
                break;
            case Key.Enter or Key.Space when current.Shape != TreeRowShape.Leaf:
                Toggle(current.Shape == TreeRowShape.Close ? current with { Shape = TreeRowShape.Open } : current);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>To the open row of the container holding the selection - its close row's own
    /// open row, for a close row.</summary>
    private void SelectParent()
    {
        var current = selection!.Current;
        TreeNode? parent = null;
        if (current.Shape == TreeRowShape.Close)
        {
            parent = current.Node;
        }
        else
        {
            foreach (var ancestor in selection.Ancestors)
                parent = ancestor.Node;
        }

        if (parent is not { } container)
            return;

        selection.SeekTo(container.RowStart);
        AfterSelectionMoved(downward: false);
    }

    /// <summary>Scrolls just enough to show the selection: to the top when it went above the
    /// view, to the bottom when it went below.</summary>
    private void AfterSelectionMoved(bool downward)
    {
        var key = selection!.Current.Key;
        int onScreen = realized.FindIndex(r => r.Key == key);
        bool fullyVisible = onScreen >= 0
            && onScreen * RowHeight - anchorPixel >= 0
            && (onScreen + 1) * RowHeight - anchorPixel <= Bounds.Height;

        if (!fullyVisible)
        {
            // Partly on screen: align to the edge it is cut by. Off screen: to the edge it left by.
            bool alignBottom = onScreen >= 0 ? (onScreen + 1) * RowHeight - anchorPixel > Bounds.Height : downward;
            anchor = selection.Clone();
            anchorPixel = 0;
            if (alignBottom)
            {
                for (int above = VisibleRowCount() - 1; above > 0 && anchor.MovePrevious(); above--)
                {
                }
            }

            Realize();
            NotifyScrollPosition();
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreeSurfaceAutomationPeer(this);
}
