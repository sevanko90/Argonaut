using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Ui.Find;
using Argonaut.Ui.Rows;
using Argonaut.Engine.Text;

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
/// (<see cref="ScrollToFraction"/>).
///
/// <b>The thumb is a fixed size</b> (<see cref="ThumbLength"/>), because nothing here knows how
/// much of the document a screen shows: a screen of expanded scalars covers a few hundred bytes,
/// a screen of collapsed containers can cover the whole file, and a thumb sized from the rows on
/// screen grew and shrank as the view moved - and, since its travel is what the size leaves,
/// shifted under a view that had not. So the thumb only says where, and its travel is the file:
/// top of the track the first byte, bottom the last. The scrollbar is the host's, and only ever follows
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
    /// The expander: Lucide's chevron-right on its 24-unit grid, stroked like the toolbar's icons
    /// and turned a quarter to point down when open. Geometry rather than a ▸/▾ glyph, which picks
    /// up font-fallback metrics that differ by platform, so the two states drew at visibly
    /// different sizes.
    /// </summary>
    private static readonly Geometry ExpanderShape = Geometry.Parse("M9 18 l6-6-6-6");
    private const double ExpanderSize = 12;
    private const double ExpanderStroke = 2.2;

    /// <summary>How long a clicked expander takes to turn.</summary>
    private static readonly TimeSpan ExpanderTurnDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>How far the expander's click target reaches into the indent before its column -
    /// the chevron is small, and the space left of it holds nothing else to click.</summary>
    public const double ExpanderReach = 8;

    /// <summary>The square drawn behind the expander under the pointer.</summary>
    private const double ExpanderPatchSize = 18;

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

    /// <summary>The scrollbar thumb's length, in pixels, whatever the document - see the remarks
    /// on the class for why it is not sized from the content.</summary>
    public const double ThumbLength = 48;

    /// <summary>A gap left between two panes, split either side of the line drawn between them.</summary>
    private const double PaneGap = 8;

    /// <summary>Space between a row's text and what trails it, and between trailing items.</summary>
    private const double TrailingGap = 12;
    private const double TrailingSpacing = 6;

    /// <summary>Space kept between pinned trailing items and the pane's right edge.</summary>
    private const double TrailingPinMargin = 8;

    // A chip: its inner padding, height and corner, and the icon it leads with - sized and
    // weighted (on Lucide's 24-unit grid) to sit with the smaller interface text inside it.
    private const double ChipPadding = 6;
    private const double ChipHeight = 17;
    private const double ChipRadius = 4;
    private const double ChipIconSize = 11;
    private const double ChipIconGap = 5;
    private const double ChipIconStroke = 2;
    private const double ChipSwatchRadius = 2.5;


    /// <summary>One note or chip after a pane's text, at <paramref name="X"/> from the text's
    /// start.</summary>
    private sealed record TrailingItem(TextLayout Label, IBrush Brush, TreeRunStyle Style, TreeRunIcon Icon, IBrush? Swatch, double X, double Width, object? Link)
    {
        public bool IsChip => Style != TreeRunStyle.Note;
    }

    /// <summary>One pane of a row as laid out: its text, the marker before its arrow, where its
    /// links are in the text, what trails the text, and how wide the whole is.</summary>
    private sealed record PaneLayout(TextLayout Layout, string Text, TextLayout? Marker,
        List<(int Start, int Length, object Link)>? Links, List<TrailingItem>? Trailing, double Width);

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
    private bool showsAll;
    private double bytesPerRow = InitialBytesPerRow;
    private ITreeRowCursor? selection;
    private string? highlightTerm;
    private TreeCurrentMatch? currentMatch;
    private IReadOnlyDictionary<TreeRunStyle, IBrush>? runBrushes;
    private IReadOnlyDictionary<TreeRowTint, IBrush>? tintBrushes;
    private IReadOnlyDictionary<TreeRunIcon, Geometry>? runIcons;
    private readonly Dictionary<IBrush, Pen> iconPens = new();

    // The row under the pointer, and which of its trailing items, if any: actions show on the
    // hovered row, and a clickable chip darkens under the pointer.
    private (long, bool)? hoveredKey;
    private int hoveredItem = -1;

    // The pane whose expander is under the pointer, on the hovered row, or -1.
    private int hoveredExpanderPane = -1;

    // The expander turning after a toggle: its row, when it started and the angle it started at.
    private (long, bool)? turningKey;
    private long turnStarted;
    private double turnFrom;
    private readonly Dictionary<IBrush, Pen> expanderPens = new();
    private Pen? swatchEdgePen;

    // Per realized row, the open containers its indent guides run down from: a span of
    // guideAncestors, each an ancestor's depth and inset (the inset arrays are shared with insets).
    private readonly List<(int Depth, double[] Inset)> guideAncestors = new();
    private readonly List<(int Start, int Count)> guideSpans = new();

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

    /// <summary>The match find is on, drawn stronger than the others, or null.</summary>
    public TreeCurrentMatch? CurrentMatch
    {
        get => currentMatch;
        set
        {
            currentMatch = value;
            InvalidateVisual();
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
            iconPens.Clear();
            DropLayouts();
            InvalidateVisual();
        }
    }

    /// <summary>The glyph per <see cref="TreeRunIcon"/>, on a 24x24 grid and drawn stroked; an
    /// icon with none leaves its chip as text alone.</summary>
    public IReadOnlyDictionary<TreeRunIcon, Geometry>? RunIcons
    {
        get => runIcons;
        set
        {
            runIcons = value;
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

    /// <summary>The font of notes and chips after a row's text - the interface font, where the
    /// rows are set in the content font. Null uses the rows' own.</summary>
    public static readonly StyledProperty<FontFamily?> ChipFontFamilyProperty =
        AvaloniaProperty.Register<TreeSurface, FontFamily?>(nameof(ChipFontFamily));

    /// <summary>Behind a chip.</summary>
    public static readonly StyledProperty<IBrush?> ChipBackgroundProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(ChipBackground));

    /// <summary>Behind a clickable chip under the pointer.</summary>
    public static readonly StyledProperty<IBrush?> ChipHoverBackgroundProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(ChipHoverBackground));

    public FontFamily? ChipFontFamily
    {
        get => GetValue(ChipFontFamilyProperty);
        set => SetValue(ChipFontFamilyProperty, value);
    }

    public IBrush? ChipBackground
    {
        get => GetValue(ChipBackgroundProperty);
        set => SetValue(ChipBackgroundProperty, value);
    }

    public IBrush? ChipHoverBackground
    {
        get => GetValue(ChipHoverBackgroundProperty);
        set => SetValue(ChipHoverBackgroundProperty, value);
    }

    /// <summary>The expander at rest. Null uses <see cref="RowSurface.Foreground"/>.</summary>
    public static readonly StyledProperty<IBrush?> ExpanderBrushProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(ExpanderBrush));

    /// <summary>The expander on the row under the pointer; under the pointer itself it takes
    /// <see cref="RowSurface.Foreground"/>.</summary>
    public static readonly StyledProperty<IBrush?> ExpanderRowHoverBrushProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(ExpanderRowHoverBrush));

    /// <summary>Whether a faint line runs down each open container's contents, from its expander.</summary>
    public static readonly StyledProperty<bool> ShowIndentGuidesProperty =
        AvaloniaProperty.Register<TreeSurface, bool>(nameof(ShowIndentGuides));

    /// <summary>The indent guides' line.</summary>
    public static readonly StyledProperty<IBrush?> GuideBrushProperty =
        AvaloniaProperty.Register<TreeSurface, IBrush?>(nameof(GuideBrush));

    public IBrush? ExpanderBrush
    {
        get => GetValue(ExpanderBrushProperty);
        set => SetValue(ExpanderBrushProperty, value);
    }

    public IBrush? ExpanderRowHoverBrush
    {
        get => GetValue(ExpanderRowHoverBrushProperty);
        set => SetValue(ExpanderRowHoverBrushProperty, value);
    }

    public bool ShowIndentGuides
    {
        get => GetValue(ShowIndentGuidesProperty);
        set => SetValue(ShowIndentGuidesProperty, value);
    }

    public IBrush? GuideBrush
    {
        get => GetValue(GuideBrushProperty);
        set => SetValue(GuideBrushProperty, value);
    }

    static TreeSurface()
    {
        AffectsRender<TreeSurface>(GutterBackgroundProperty, DividerBrushProperty, ChipBackgroundProperty, ChipHoverBackgroundProperty,
            ExpanderBrushProperty, ExpanderRowHoverBrushProperty, ShowIndentGuidesProperty, GuideBrushProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChipFontFamilyProperty)
            OnTextStyleChanged();
        else if (change.Property == ExpanderBrushProperty || change.Property == ExpanderRowHoverBrushProperty || change.Property == ForegroundProperty)
            expanderPens.Clear();
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

        for (int pane = 0; pane < laid.Panes.Length; pane++)
        {
            var laidPane = laid.Panes[pane];
            if (laidPane.Trailing?.Find(item => item.Link is not null) is { } item)
            {
                double textX = ArrowLeft(index, pane) + ToggleWidth;
                double shift = TrailingShift(laidPane, textX, PaneLeft(pane) + PaneWidth);
                return new Rect(textX + shift + item.X, ChipTop(index * RowHeight - anchorPixel), item.Width, ChipHeight);
            }
        }

        return null;
    }

    /// <summary>Whether realized row <paramref name="index"/> shows its actions, for tests.</summary>
    internal bool ShowsActions(int index) => ShowsActions(realized[index]);

    /// <summary>The text layout of one pane of realized row <paramref name="index"/>, for tests.</summary>
    internal TextLayout PaneTextLayout(int index, int pane = 0)
        => LayoutFor(realized[index], new Typeface(FontFamily), FontSize).Panes[pane].Layout;

    /// <summary>Where realized row <paramref name="index"/>'s arrow is drawn in a pane, for tests.</summary>
    internal double ArrowX(int index, int pane = 0) => ArrowLeft(index, pane);

    /// <summary>Test hook: where realized row <paramref name="index"/>'s indent guides run, or
    /// none while guides are off.</summary>
    internal IReadOnlyList<double> IndentGuideXs(int index, int pane = 0)
    {
        if (!ShowIndentGuides)
            return [];

        var (start, count) = guideSpans[index];
        var xs = new double[count];
        for (int i = 0; i < count; i++)
            xs[i] = GuideX(guideAncestors[start + i], pane);
        return xs;
    }

    /// <summary>Test hook: the angle realized row <paramref name="index"/>'s chevron is drawn at.</summary>
    internal double ExpanderTurnAt(int index) => ExpanderTurn(realized[index]);

    /// <summary>Test hook: whether a chevron is turning.</summary>
    internal bool IsExpanderTurning => turningKey is not null;

    /// <summary>Test hook: finishes a turn the headless platform has no frames to run.</summary>
    internal void EndExpanderTurn()
    {
        turningKey = null;
        InvalidateVisual();
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
        guideAncestors.Clear();
        guideSpans.Clear();
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

            guideSpans.Add((guideAncestors.Count, open.Count));
            guideAncestors.AddRange(open);

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

        StartTurn(row);
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
        showsAll = false;
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

        // Asked only at the end, so scrolling through the middle never pays for the step back.
        showsAll = showsEnd && anchorPixel == 0 && !anchor.Clone().MovePrevious();

        ComputeInsets();
        EstimateBytesPerRow();
        PruneLayouts();
        MeasureRealizedRows();
    }

    /// <summary>Refines the bytes a row covers from the rows on screen - what a part-scrolled top
    /// row adds to the position - smoothed so the position does not lurch with every screen.</summary>
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
            widest = Math.Max(widest, row.Depth * IndentWidth + insets[i][0] + ToggleWidth + laid.Width);
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
        List<TreeRun>? trailingRuns = null;
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
                continue;

            if (run.Style is TreeRunStyle.Note or TreeRunStyle.Chip or TreeRunStyle.Action)
            {
                (trailingRuns ??= new()).Add(run);
                continue;
            }

            var brush = runBrushes is not null && runBrushes.TryGetValue(run.Style, out var styled) ? styled : foreground;
            overrides.Add(new ValueSpan<TextRunProperties>(text.Length, run.Text.Length,
                new GenericTextRunProperties(typeface, fontSize, foregroundBrush: brush)));
            if (run.Link is { } link)
                (links ??= new()).Add((text.Length, run.Text.Length, link));
            text.Append(run.Text);
        }

        // A separator a value may hold unescaped (U+2028 and kin) would break the layout onto a
        // second line inside one row band, so it is drawn as a glyph; the length is unchanged, so
        // the runs and links above still line up.
        string content = ControlGlyphs.ForDisplay(text.ToString());
        var layout = new TextLayout(content, typeface, fontSize, foreground, TextAlignment.Left, TextWrapping.NoWrap,
            textStyleOverrides: overrides);

        TextLayout? marker = null;
        if (markerLabel is { } label)
        {
            var markerBrush = runBrushes is not null && runBrushes.TryGetValue(TreeRunStyle.Hint, out var muted) ? muted : foreground;
            marker = new TextLayout(label, typeface, Math.Max(6, fontSize * 0.75), markerBrush);
        }

        double textWidth = layout.WidthIncludingTrailingWhitespace;
        var trailing = trailingRuns is null ? null : LayOutTrailing(trailingRuns, textWidth, fontSize, foreground);
        double width = trailing is { Count: > 0 } ? trailing[^1].X + trailing[^1].Width : textWidth;
        return new PaneLayout(layout, content, marker, links, trailing, width);
    }

    /// <summary>Lays the notes and chips after a pane's text out in a row, a gap after it.</summary>
    private List<TrailingItem> LayOutTrailing(List<TreeRun> trailingRuns, double textWidth, double fontSize, IBrush foreground)
    {
        var typeface = new Typeface(ChipFontFamily ?? FontFamily);
        double labelSize = Math.Max(6, fontSize - 1);
        var items = new List<TrailingItem>(trailingRuns.Count);
        double x = textWidth + TrailingGap;
        foreach (var run in trailingRuns)
        {
            var brush = runBrushes is not null && runBrushes.TryGetValue(run.Style, out var styled) ? styled : foreground;
            var label = new TextLayout(ControlGlyphs.ForDisplay(run.Text), typeface, labelSize, brush);
            bool isChip = run.Style != TreeRunStyle.Note;
            var swatch = isChip && run.Swatch is { } colour ? new ImmutableSolidColorBrush(colour) : null;
            var icon = swatch is null && isChip && runIcons is not null && runIcons.ContainsKey(run.Icon) ? run.Icon : TreeRunIcon.None;
            double width = label.WidthIncludingTrailingWhitespace
                + (isChip ? 2 * ChipPadding : 0)
                + (icon != TreeRunIcon.None || swatch is not null ? ChipIconSize + ChipIconGap : 0);

            items.Add(new TrailingItem(label, brush, run.Style, icon, swatch, x, width, run.Link));
            x += width + TrailingSpacing;
        }

        return items;
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

        if (ShowIndentGuides && GuideBrush is { } guideBrush)
            DrawGuides(context, guideBrush, index, pane, y);

        if (laid.Marker is { } marker)
            marker.Draw(context, new Point(arrowX - 2 - marker.WidthIncludingTrailingWhitespace, CentreInRow(marker, y)));

        if (DrawsArrow(row, laid))
            DrawExpander(context, row, pane, arrowX, y, foreground);

        double textTop = CentreInRow(laid.Layout, y);
        double textX = arrowX + ToggleWidth;
        int current = pane == 0 && currentMatch is { } match && match.RowKey == row.Key ? match.Occurrence : -1;
        DrawHighlights(context, laid.Layout, laid.Text, textX, textTop, y, current);
        laid.Layout.Draw(context, new Point(textX, textTop));
        if (current >= 0)
            DrawCurrentMatch(context, laid.Layout, laid.Text, textX, textTop, y, current);

        if (laid.Trailing is { } trailing)
        {
            double shift = TrailingShift(laid, textX, paneRight);
            if (shift < 0)
                MaskUnderPinned(context, row, textX + shift + trailing[0].X - TrailingGap, paneRight, y);
            DrawTrailing(context, row, trailing, textX + shift, y);
        }
    }

    /// <summary>
    /// How far a pane's trailing items move left so they end inside it. A row whose text runs past
    /// the edge - a value cut at the display cap - keeps its size note and its actions in view,
    /// over the end of the text, rather than out where only panning would find them.
    /// </summary>
    private static double TrailingShift(PaneLayout laid, double textX, double paneRight)
    {
        if (laid.Trailing is not { Count: > 0 } trailing)
            return 0;

        double overflow = textX + laid.Width + TrailingPinMargin - paneRight;
        return overflow <= 0 ? 0 : -Math.Min(overflow, trailing[0].X);
    }

    /// <summary>Covers the text pinned trailing items sit over, in whatever the row shows behind
    /// its text, so the two do not draw through each other.</summary>
    private void MaskUnderPinned(DrawingContext context, in TreeRow row, double left, double paneRight, double y)
    {
        var area = new Rect(left, y, Math.Max(0, paneRight - left), RowHeight);
        if (Background is { } background)
            context.FillRectangle(background, area);
        if (SelectionBrush is { } selected && selection is { } cursor && row.Key == cursor.Current.Key)
            context.FillRectangle(selected, area);
    }

    /// <summary>A row's share of its indent guides: a line through it below each open ancestor's
    /// expander.</summary>
    private void DrawGuides(DrawingContext context, IBrush brush, int index, int pane, double y)
    {
        var (start, count) = guideSpans[index];
        for (int i = start; i < start + count; i++)
            context.FillRectangle(brush, new Rect(GuideX(guideAncestors[i], pane), y, 1, RowHeight));
    }

    /// <summary>Where a guide runs: down the middle of the ancestor's expander, on a whole pixel.</summary>
    private double GuideX((int Depth, double[] Inset) ancestor, int pane)
        => Math.Floor(PaneLeft(pane) + ancestor.Depth * IndentWidth + ancestor.Inset[pane] - Pan + ToggleWidth / 2);

    /// <summary>The chevron, faint at rest, stronger on the hovered row and strongest under the
    /// pointer, where a patch behind it shows how far its click target reaches.</summary>
    private void DrawExpander(DrawingContext context, in TreeRow row, int pane, double arrowX, double y, IBrush foreground)
    {
        bool isHovered = row.Key == hoveredKey;
        bool isHot = isHovered && pane == hoveredExpanderPane;
        double centreX = arrowX + ToggleWidth / 2;
        double centreY = y + RowHeight / 2;

        if (isHot && ChipBackground is { } patch)
        {
            var area = new Rect(centreX - ExpanderPatchSize / 2, centreY - ExpanderPatchSize / 2, ExpanderPatchSize, ExpanderPatchSize);
            context.DrawRectangle(patch, null, new RoundedRect(area, ChipRadius));
        }

        var brush = isHot ? foreground
            : isHovered ? ExpanderRowHoverBrush ?? ExpanderBrush ?? foreground
            : ExpanderBrush ?? foreground;

        double scale = ExpanderSize / 24;
        var at = Matrix.CreateTranslation(-12, -12)
            * Matrix.CreateRotation(ExpanderTurn(row) * Math.PI / 180)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(centreX, centreY);
        using (context.PushTransform(at))
            context.DrawGeometry(null, ExpanderPen(brush), ExpanderShape);
    }

    private Pen ExpanderPen(IBrush brush)
    {
        if (!expanderPens.TryGetValue(brush, out var pen))
            expanderPens[brush] = pen = new Pen(brush, ExpanderStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        return pen;
    }

    /// <summary>The angle a row's chevron is drawn at, in degrees: 0 pointing right, 90 down,
    /// and between the two while it turns.</summary>
    private double ExpanderTurn(in TreeRow row)
    {
        double rest = row.IsExpanded ? 90 : 0;
        if (row.Key != turningKey)
            return rest;

        double t = Math.Min(1, Stopwatch.GetElapsedTime(turnStarted) / ExpanderTurnDuration);
        double eased = 1 - Math.Pow(1 - t, 3);
        return turnFrom + (rest - turnFrom) * eased;
    }

    /// <summary>Turns a row's chevron from where it is now, ahead of the row toggling. Only that
    /// row moves: the rows it opens or closes appear and go at once.</summary>
    private void StartTurn(in TreeRow row)
    {
        if (row.Shape != TreeRowShape.Open)
            return;

        turnFrom = ExpanderTurn(row);
        turningKey = row.Key;
        turnStarted = Stopwatch.GetTimestamp();
        RequestTurnFrame();
    }

    /// <summary>Frames rather than a timer, as the raw view's arrival flash: the turn redraws in
    /// step with the display, and asks for nothing once it is over.</summary>
    private void RequestTurnFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (turningKey is null)
            return;

        if (Stopwatch.GetElapsedTime(turnStarted) >= ExpanderTurnDuration)
            turningKey = null;
        else
            RequestTurnFrame();

        InvalidateVisual();
    });

    /// <summary>The expander's click target in a pane: its column, and the indent before it up
    /// to a marker, whose label sits just left of the column.</summary>
    private static bool IsOnExpander(PaneLayout laid, double arrowX, double x)
        => x >= arrowX - (laid.Marker is null ? ExpanderReach : 2) && x < arrowX + ToggleWidth;

    /// <summary>The notes and chips after a pane's text; actions only where they show.</summary>
    private void DrawTrailing(DrawingContext context, in TreeRow row, List<TrailingItem> trailing, double textX, double y)
    {
        bool showsActions = ShowsActions(row);
        bool isHovered = row.Key == hoveredKey;
        for (int i = 0; i < trailing.Count; i++)
        {
            var item = trailing[i];
            if (item.Style == TreeRunStyle.Action && !showsActions)
                continue;

            double labelX = textX + item.X;
            if (item.IsChip)
            {
                bool isHot = isHovered && i == hoveredItem && item.Link is not null;
                if ((isHot ? ChipHoverBackground ?? ChipBackground : ChipBackground) is { } fill)
                    context.DrawRectangle(fill, null, new RoundedRect(new Rect(labelX, ChipTop(y), item.Width, ChipHeight), ChipRadius));

                labelX += ChipPadding;
                if (item.Swatch is { } swatch)
                {
                    DrawSwatch(context, swatch, item.Brush, new Rect(labelX, y + (RowHeight - ChipIconSize) / 2, ChipIconSize, ChipIconSize));
                    labelX += ChipIconSize + ChipIconGap;
                }
                else if (item.Icon != TreeRunIcon.None && runIcons!.TryGetValue(item.Icon, out var glyph))
                {
                    double scale = ChipIconSize / 24;
                    var at = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(labelX, y + (RowHeight - ChipIconSize) / 2);
                    using (context.PushTransform(at))
                        context.DrawGeometry(null, IconPen(item.Brush), glyph);
                    labelX += ChipIconSize + ChipIconGap;
                }
            }

            item.Label.Draw(context, new Point(labelX, CentreInRow(item.Label, y)));
        }
    }

    private static double ChipTop(double rowTop) => rowTop + (RowHeight - ChipHeight) / 2;

    /// <summary>
    /// A colour swatch in a chip: a checker under it, so a translucent colour shows as one, and a
    /// faint edge in the chip's text colour, so a colour close to the chip's own still has a shape.
    /// </summary>
    private void DrawSwatch(DrawingContext context, IBrush swatch, IBrush edge, Rect area)
    {
        var shape = new RoundedRect(area, ChipSwatchRadius);
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
            context.DrawRectangle(null, SwatchEdgePen(edge), new RoundedRect(area.Deflate(0.5), ChipSwatchRadius));
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
            iconPens[brush] = pen = new Pen(brush, ChipIconStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        return pen;
    }

    /// <summary>Whether a row shows its actions: the row under the pointer, and the selected one -
    /// so a row picked from the keyboard offers them too.</summary>
    private bool ShowsActions(in TreeRow row)
        => row.Key == hoveredKey || (selection is { } selected && row.Key == selected.Current.Key);

    /// <summary>A pill behind each find match in a row's text; the current one is drawn over
    /// the text afterwards, by <see cref="DrawCurrentMatch"/>.</summary>
    private void DrawHighlights(DrawingContext context, TextLayout layout, string text, double x, double textTop, double rowTop, int current)
    {
        if (SearchTextSplitter.Split(text, highlightTerm) is not { } segments)
            return;

        int occurrence = 0;
        foreach (var segment in segments)
        {
            if (!segment.IsMatch)
                continue;

            bool isCurrent = occurrence++ == current;
            foreach (var rect in layout.HitTestTextRange(segment.Start, segment.Length))
            {
                var at = rect.Translate(new Vector(x, textTop));
                if (isCurrent)
                    DrawCurrentMatchPill(context, at, rowTop, text.Substring(segment.Start, segment.Length), overText: false);
                else
                    DrawMatchPill(context, at, rowTop);
            }
        }
    }

    /// <summary>The current match, over the row's text.</summary>
    private void DrawCurrentMatch(DrawingContext context, TextLayout layout, string text, double x, double textTop, double rowTop, int current)
    {
        if (SearchTextSplitter.Split(text, highlightTerm) is not { } segments)
            return;

        int occurrence = 0;
        foreach (var segment in segments)
        {
            if (!segment.IsMatch || occurrence++ != current)
                continue;

            foreach (var rect in layout.HitTestTextRange(segment.Start, segment.Length))
                DrawCurrentMatchPill(context, rect.Translate(new Vector(x, textTop)), rowTop, text.Substring(segment.Start, segment.Length), overText: true);
            return;
        }
    }

    // ---- scrolling ------------------------------------------------------------------------

    // The estimated model (see RowSurface): the position is the anchor's scroll position in the
    // source - for a document, its byte offset - since there is no row count to take a fraction of.

    /// <summary>The anchor's scroll position as a fraction of the source's, plus the part of a row
    /// scrolled off the top - laid over the thumb's travel, so the whole file spans the track.</summary>
    public override double ScrollFraction
    {
        get
        {
            long length = document?.ScrollLength ?? 0;
            if (anchor is null || length <= 0)
                return 0;

            double position = document!.ScrollPosition(anchor.Current) + anchorPixel / RowHeight * bytesPerRow;
            return Math.Clamp(position / length, 0, 1) * ThumbTravel;
        }
    }

    /// <summary>A fixed <see cref="ThumbLength"/> of the track, or all of it when a screen shows
    /// every row.</summary>
    public override double ViewportFraction
    {
        get
        {
            if (showsAll || (document?.ScrollLength ?? 0) <= 0 || Bounds.Height <= 0)
                return 1;

            return Math.Clamp(ThumbLength / Bounds.Height, 0, 0.5);
        }
    }

    /// <summary>The share of the track the thumb's top can move over - what a fraction of the
    /// file is scaled by on the way to the scrollbar, and back on the way from it.</summary>
    private double ThumbTravel => 1 - ViewportFraction;

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
    /// Puts the row at <paramref name="fraction"/> of the thumb's travel at the top - what a
    /// dragged thumb does; the travel spans the file, so the track's middle is the file's. At the end, the last row settles at the bottom. How far a seek may reach while
    /// the rows are still being worked out is the source's to say
    /// (<see cref="ITreeRowSource.SeekScrollPosition"/>).
    /// </summary>
    public override void ScrollToFraction(double fraction)
    {
        if (anchor is null || document is null)
            return;

        double travel = ThumbTravel;
        double ofFile = travel > 0 ? fraction / travel : 0;
        document.SeekScrollPosition(anchor, (long)(Math.Clamp(ofFile, 0, 1) * document.ScrollLength));

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

        double shift = TrailingShift(laid, arrowX + ToggleWidth, PaneLeft(pane) + PaneWidth);
        if (LinkAt(laid, point.X - arrowX - ToggleWidth, point.Y - CentreInRow(laid.Layout, index * RowHeight - anchorPixel), shift) is { } link)
        {
            Select(row);
            LinkClicked?.Invoke(this, new TreeLinkClickedEventArgs(row, link));
            e.Handled = true;
            return;
        }

        Select(row);

        bool onArrow = DrawsArrow(row, laid) && IsOnExpander(laid, arrowX, point.X);
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
        bool overLink = false;
        if (!overEdge && document is not null && RowAt(point.Y) is { } hovered)
        {
            var laid = LayoutFor(hovered.Row, new Typeface(FontFamily), FontSize).Panes[pane];
            double arrowX = ArrowLeft(hovered.Index, pane);
            double textX = arrowX + ToggleWidth;
            double x = point.X - textX;
            double shift = TrailingShift(laid, textX, PaneLeft(pane) + PaneWidth);
            overLink = LinkAt(laid, x, point.Y - CentreInRow(laid.Layout, hovered.Index * RowHeight - anchorPixel), shift) is not null;
            bool overExpander = DrawsArrow(hovered.Row, laid) && IsOnExpander(laid, arrowX, point.X);
            SetHover(hovered.Row.Key, TrailingAt(laid, x, shift), overExpander ? pane : -1);
        }
        else
        {
            SetHover(null, -1, -1);
        }

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
        SetHover(null, -1, -1);
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

    /// <summary>The link under a point given in the row text's own coordinates, with the
    /// trailing items moved by <paramref name="trailingShift"/> (see <see cref="TrailingShift"/>).</summary>
    private static object? LinkAt(PaneLayout laid, double x, double y, double trailingShift)
    {
        if (TrailingAt(laid, x, trailingShift) is var item and >= 0)
            return laid.Trailing![item].Link;

        // Text covered by pinned trailing items is not there to click.
        if (trailingShift < 0 && x >= laid.Trailing![0].X + trailingShift - TrailingGap)
            return null;

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

    /// <summary>The trailing item under an x given from the row text's start, or -1. An action is
    /// hit whether or not it is drawn: the pointer being there makes its row the hovered one.</summary>
    private static int TrailingAt(PaneLayout laid, double x, double trailingShift)
    {
        if (laid.Trailing is not { } trailing)
            return -1;

        x -= trailingShift;
        for (int i = 0; i < trailing.Count; i++)
        {
            if (x >= trailing[i].X && x < trailing[i].X + trailing[i].Width)
                return i;
        }

        return -1;
    }

    private void SetHover((long, bool)? key, int item, int expanderPane)
    {
        if (key == hoveredKey && item == hoveredItem && expanderPane == hoveredExpanderPane)
            return;

        hoveredKey = key;
        hoveredItem = item;
        hoveredExpanderPane = expanderPane;
        InvalidateVisual();
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

        StartTurn(row);
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
