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

    /// <summary>A gap left between two panes, split either side of the line drawn between them.</summary>
    private const double PaneGap = 8;

    /// <summary>One pane of a row as laid out: its text, the marker before its arrow, and where
    /// its links are in the text.</summary>
    private sealed record PaneLayout(TextLayout Layout, string Text, TextLayout? Marker, List<(int Start, int Length, object Link)>? Links);

    /// <summary>One row as laid out: a pane per side the painter draws, one for a plain tree.</summary>
    private sealed record RowLayout(PaneLayout[] Panes)
    {
        public PaneLayout First => Panes[0];
    }

    private readonly List<TreeRow> realized = new();

    // Per realized row and pane, how far its arrow is set in beyond its depth's indent: what the
    // markers of it and its ancestors add.
    private readonly List<double[]> insets = new();
    private readonly Dictionary<(long, TreeRowShape, bool), RowLayout> layouts = new();
    private readonly List<TreeRun> runs = new();
    private IResizableTreeGutter? resizing;
    private double resizeStartX;
    private double resizeStartWidth;
    private object? toolTipShown;
    private DispatcherTimer? toolTipDelay;

    private ITreeRowSource? document;
    private ITreeRowCursor? anchor;
    private double anchorPixel;
    private bool showsEnd;
    private double bytesPerRow = InitialBytesPerRow;
    private ITreeRowCursor? selection;
    private string? highlightTerm;
    private IReadOnlyDictionary<TreeRunStyle, IBrush>? runBrushes;
    private IReadOnlyDictionary<TreeRowTint, IBrush>? tintBrushes;

    /// <summary>The rows shown. Setting them resets the view to the top.</summary>
    public ITreeRowSource? Document
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

    /// <summary>The wash behind a pane per <see cref="TreeRowTint"/>; a tint with none is not
    /// drawn.</summary>
    public IReadOnlyDictionary<TreeRowTint, IBrush>? TintBrushes
    {
        get => tintBrushes;
        set
        {
            tintBrushes = value;
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
        for (int pane = 0; pane < laid.Panes.Length; pane++)
        {
            var laidPane = laid.Panes[pane];
            if (laidPane.Links is not { Count: > 0 } links)
                continue;

            double textX = ArrowLeft(index, pane) + ToggleWidth;
            double textTop = CentreInRow(laidPane.Layout, index * RowHeight - anchorPixel);
            foreach (var rect in laidPane.Layout.HitTestTextRange(links[0].Start, links[0].Length))
                return rect.Translate(new Vector(textX, textTop));
        }

        return null;
    }

    /// <summary>Where realized row <paramref name="index"/>'s arrow is drawn in a pane, for tests.</summary>
    internal double ArrowX(int index, int pane = 0) => ArrowLeft(index, pane);

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
    public override double PanViewportWidth => Math.Max(0, Bounds.Width - ContentLeft - ContentPaddingX);

    public override double PanStep => IndentWidth * 2;

    private int PaneCount => Math.Max(1, document?.Painter.PaneCount ?? 1);

    /// <summary>How far a pane reaches across the rows' area: all of it for a plain tree, an equal
    /// share less the gap between panes when there are several.</summary>
    private double PaneWidth
    {
        get
        {
            double area = Math.Max(0, Bounds.Width - ContentLeft - ContentPaddingX);
            int panes = PaneCount;
            return panes == 1 ? area : Math.Max(0, area / panes - PaneGap);
        }
    }

    private double PaneLeft(int pane)
        => pane == 0 ? ContentLeft : ContentLeft + pane * (PaneWidth + PaneGap);

    /// <summary>Only a single pane pans: side-by-side panes each clip to their own share.</summary>
    private double Pan => PaneCount == 1 ? PanOffset : 0;

    /// <summary>Where realized row <paramref name="index"/>'s arrow sits in a pane, panned: after
    /// its indent and the markers of it and its ancestors.</summary>
    private double ArrowLeft(int index, int pane)
        => PaneLeft(pane) + realized[index].Depth * IndentWidth + insets[index][pane] - Pan;

    /// <summary>
    /// Works out each realized row's inset. A marked row steps in from its parent by the marker's
    /// width rather than the indent - room for the marker before its arrow - and its children
    /// carry that on, so the members of an array element sit right of the element's arrow rather
    /// than left of it. Only the difference is carried, so nested arrays step in a marker's width
    /// a level, not a marker and an indent. A closing row takes its container's. The walk starts
    /// from the top row's ancestors, since they are not on screen.
    /// </summary>
    private void ComputeInsets()
    {
        insets.Clear();
        if (realized.Count == 0 || document is null || anchor is null)
            return;

        var painter = document.Painter;
        int panes = PaneCount;
        var none = new double[panes];
        var open = new List<(int Depth, double[] Inset)>();

        double[] Inset(double[] parent, in TreeRow row)
        {
            var inset = (double[])parent.Clone();
            for (int pane = 0; pane < panes; pane++)
            {
                if (painter.PaneMarker(row, pane) is not null)
                    inset[pane] += MarkerWidth - IndentWidth;
            }

            return inset;
        }

        foreach (var ancestor in anchor.Ancestors)
            open.Add((ancestor.Depth, Inset(open.Count > 0 ? open[^1].Inset : none, ancestor)));

        foreach (var row in realized)
        {
            while (open.Count > 0 && open[^1].Depth >= row.Depth)
                open.RemoveAt(open.Count - 1);

            var parent = open.Count > 0 ? open[^1].Inset : none;
            var inset = Inset(parent, row.Shape == TreeRowShape.Close ? row with { Shape = TreeRowShape.Open } : row);
            insets.Add(inset);
            if (row is { Shape: TreeRowShape.Open, IsExpanded: true })
                open.Add((row.Depth, inset));
        }
    }

    /// <summary>Whether a pane draws the row's expand arrow: always for a plain tree, and in a
    /// pane only where the row has something on that side.</summary>
    private bool DrawsArrow(in TreeRow row, PaneLayout layout)
        => row.Shape == TreeRowShape.Open && (PaneCount == 1 || layout.Text.Length > 0);

    /// <summary>The pane under a surface x.</summary>
    private int PaneAt(double x)
    {
        int panes = PaneCount;
        if (panes == 1)
            return 0;

        return Math.Clamp((int)Math.Floor((x - ContentLeft) / (PaneWidth + PaneGap)), 0, panes - 1);
    }

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
               && document.Hides(hidden, offset))
        {
            document.SetExpanded(hidden, true);
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

        document.Toggle(row);
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
        insets.Clear();
        showsEnd = false;
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
        showsEnd = !more && realized.Count * RowHeight - anchorPixel <= Bounds.Height + 0.5;

        int fitting = Math.Max(1, (int)(Bounds.Height / RowHeight));
        if (realized.Count < needed && realized.Count < fitting)
        {
            anchorPixel = 0;
            while (realized.Count < fitting && anchor.MovePrevious())
                realized.Insert(0, anchor.Current);
        }

        ComputeInsets();
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

        long span = document!.ScrollPosition(realized[^1]) - document.ScrollPosition(realized[0]);
        if (span <= 0)
            return;

        double measured = (double)span / (realized.Count - 1);
        bytesPerRow = Math.Max(1, bytesPerRow * 0.75 + measured * 0.25);
    }

    private int VisibleRowCount() => Math.Max(1, (int)(Bounds.Height / RowHeight));

    /// <summary>Records how wide the rows on screen need the text column to be - for a plain
    /// tree only, since side-by-side panes clip rather than pan.</summary>
    private void MeasureRealizedRows()
    {
        if (PaneCount != 1)
            return;

        var typeface = new Typeface(FontFamily);
        double widest = WidestRowWidth;
        for (int i = 0; i < realized.Count; i++)
        {
            var row = realized[i];
            var laid = LayoutFor(row, typeface, FontSize).First;
            widest = Math.Max(widest, row.Depth * IndentWidth + insets[i][0] + ToggleWidth + laid.Layout.WidthIncludingTrailingWhitespace);
        }

        RecordRowWidth(widest);
    }

    private RowLayout LayoutFor(in TreeRow row, Typeface typeface, double fontSize)
    {
        var key = (row.Node.ValueStart, row.Shape, row.IsExpanded);
        if (layouts.TryGetValue(key, out var cached))
            return cached;

        var painter = document!.Painter;
        var panes = new PaneLayout[PaneCount];
        for (int pane = 0; pane < panes.Length; pane++)
        {
            runs.Clear();
            painter.AppendPaneRuns(row, pane, runs);
            panes[pane] = LayOutPane(runs, painter.PaneMarker(row, pane), typeface, fontSize);
        }

        var entry = new RowLayout(panes);
        layouts[key] = entry;
        return entry;
    }

    private PaneLayout LayOutPane(List<TreeRun> runs, string? markerLabel, Typeface typeface, double fontSize)
    {
        var foreground = Foreground ?? Brushes.Black;
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
        if (markerLabel is { } label)
        {
            var markerBrush = runBrushes is not null && runBrushes.TryGetValue(TreeRunStyle.Hint, out var muted) ? muted : foreground;
            marker = new TextLayout(label, typeface, Math.Max(6, fontSize * 0.75), markerBrush);
        }

        return new PaneLayout(layout, content, marker, links);
    }

    /// <summary>What the row says, every pane's text in order.</summary>
    private string RowText(in TreeRow row)
    {
        runs.Clear();
        if (document is { } source)
        {
            for (int pane = 0; pane < PaneCount; pane++)
                source.Painter.AppendPaneRuns(row, pane, runs);
        }

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
        double paneWidth = PaneWidth;
        int paneCount = PaneCount;

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

            var laid = LayoutFor(row, typeface, fontSize);
            for (int pane = 0; pane < paneCount; pane++)
            {
                double paneLeft = PaneLeft(pane);
                using (context.PushClip(new Rect(paneLeft, y, paneWidth, RowHeight)))
                    DrawPane(context, i, laid.Panes[pane], pane, y, paneLeft + paneWidth, foreground);
            }
        }

        // The line between side-by-side panes, down the middle of the gap between them.
        if (paneCount > 1 && DividerBrush is { } paneDivider)
        {
            for (int pane = 1; pane < paneCount; pane++)
                context.FillRectangle(paneDivider, new Rect(PaneLeft(pane) - PaneGap / 2, 0, 1, Bounds.Height));
        }
    }

    /// <summary>One pane of one row: its tint, marker, arrow, highlights and text.</summary>
    private void DrawPane(DrawingContext context, int index, PaneLayout laid, int pane, double y, double paneRight, IBrush foreground)
    {
        var row = realized[index];
        double arrowX = ArrowLeft(index, pane);

        if (tintBrushes is not null && document!.Painter.PaneTint(row, pane) is var tint and not TreeRowTint.None
            && tintBrushes.TryGetValue(tint, out var wash))
        {
            context.DrawRectangle(wash, null, new RoundedRect(new Rect(arrowX, y + 1, Math.Max(0, paneRight - arrowX), RowHeight - 2), 4));
        }

        if (laid.Marker is { } marker)
            marker.Draw(context, new Point(arrowX - 2 - marker.WidthIncludingTrailingWhitespace, CentreInRow(marker, y)));

        if (DrawsArrow(row, laid))
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

    // The estimated model (see RowSurface): the position is the anchor's scroll position in the
    // source - for a document, its byte offset - since there is no row count to take a fraction of.

    /// <summary>The anchor's scroll position as a fraction of the source's, plus the part of a row
    /// scrolled off the top.</summary>
    public override double ScrollFraction
    {
        get
        {
            long length = document?.ScrollLength ?? 0;
            if (anchor is null || length <= 0)
                return 0;

            double position = document!.ScrollPosition(anchor.Current) + anchorPixel / RowHeight * bytesPerRow;
            return Math.Clamp(position / length, 0, 1);
        }
    }

    /// <summary>From the average scroll range a row covers - smoothed, so a scrollbar thumb sized
    /// from it does not flicker.</summary>
    public override double ViewportFraction
    {
        get
        {
            long length = document?.ScrollLength ?? 0;
            return length <= 0 ? 1 : Math.Clamp(VisibleRowCount() * bytesPerRow / length, 0, 1);
        }
    }

    public override bool ShowsEnd => showsEnd;

    /// <summary>While the rows are still being worked out, the furthest they have reached.</summary>
    public override void ScrollToEnd()
    {
        if (anchor is null || document is null)
            return;

        if (!document.IsComplete)
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
    /// Puts the row at <paramref name="fraction"/> of the scroll range at the top - what a dragged
    /// thumb does. At the end, the last row settles at the bottom. How far a seek may reach while
    /// the rows are still being worked out is the source's to say
    /// (<see cref="ITreeRowSource.SeekScrollPosition"/>).
    /// </summary>
    public override void ScrollToFraction(double fraction)
    {
        if (anchor is null || document is null)
            return;

        document.SeekScrollPosition(anchor, (long)(Math.Clamp(fraction, 0, 1) * document.ScrollLength));

        anchorPixel = 0;
        Realize();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    public override void ScrollByPixels(double delta)
    {
        if (anchor is null)
            return;

        ScrollBy(delta);
        NotifyScrollPosition();
        InvalidateVisual();
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
        int pane = PaneAt(point.X);
        var laid = LayoutFor(row, new Typeface(FontFamily), FontSize).Panes[pane];
        double arrowX = ArrowLeft(index, pane);

        if (LinkAt(laid, point.X - arrowX - ToggleWidth, point.Y - CentreInRow(laid.Layout, index * RowHeight - anchorPixel)) is { } link)
        {
            Select(row);
            LinkClicked?.Invoke(this, new TreeLinkClickedEventArgs(row, link));
            e.Handled = true;
            return;
        }

        Select(row);

        bool onArrow = DrawsArrow(row, laid) && point.X >= arrowX && point.X < arrowX + ToggleWidth;
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
        int pane = PaneAt(point.X);
        bool overLink = !overEdge && RowAt(point.Y) is { } hovered
            && LayoutFor(hovered.Row, new Typeface(FontFamily), FontSize).Panes[pane] is var laid
            && LinkAt(laid, point.X - ArrowLeft(hovered.Index, pane) - ToggleWidth,
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
    private static object? LinkAt(PaneLayout laid, double x, double y)
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

        if (row.IsExpanded || row.Shape == TreeRowShape.Close)
            document.CollapseDeep(row);
        else if (!document.ExpandDeep(row, DeepExpandRowBudget))
            ExpandLimitReached?.Invoke(this, EventArgs.Empty);

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
        long? parent = null;
        if (current.Shape == TreeRowShape.Close)
        {
            parent = current.Node.RowStart;
        }
        else
        {
            foreach (var ancestor in selection.Ancestors)
                parent = ancestor.Start;
        }

        if (parent is not { } parentStart)
            return;

        selection.SeekTo(parentStart);
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
