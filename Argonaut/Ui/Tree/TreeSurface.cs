using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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
/// surface holds a cursor on its top row (the anchor) and walks from it to fill the viewport. The
/// scroll position a host sees is an estimate - the anchor's byte position over the average bytes
/// a row covers on screen - so the scrollbar's thumb is where the view is in the file. A small
/// change of offset (the wheel, arrow keys, page keys) moves the anchor that many rows; a large
/// one (dragging the thumb) seeks to the byte it points at. After a relative move the offset is
/// re-synced to the anchor's estimated position, so the estimate may drift while the rows on
/// screen never jump.
/// </summary>
public class TreeSurface : RowSurface
{
    /// <summary>Horizontal step per nesting level.</summary>
    public const double IndentWidth = 16;

    /// <summary>Width of the expand-arrow column before each row's text.</summary>
    public const double ToggleWidth = 16;

    private const string ExpandedGlyph = "▾";
    private const string CollapsedGlyph = "▸";

    /// <summary>A row covers this many bytes until rows on screen say otherwise.</summary>
    private const double InitialBytesPerRow = 32;

    private readonly List<TreeRow> realized = new();
    private readonly Dictionary<(long, TreeRowShape, bool), (TextLayout Layout, string Text)> layouts = new();
    private readonly List<TreeRun> runs = new();

    private TreeDocument? document;
    private TreeCursor? anchor;
    private double anchorPixel;
    private double lastOffsetY;
    private bool syncingOffset;
    private double bytesPerRow = InitialBytesPerRow;
    private TreeCursor? selection;
    private TextLayout? expandedArrow;
    private TextLayout? collapsedArrow;
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
                document.Grew -= OnDocumentGrew;
            document = value;
            if (document is not null)
                document.Grew += OnDocumentGrew;

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

    /// <summary>A container was expanded or collapsed from the surface.</summary>
    public event EventHandler? ExpansionChanged;

    /// <summary>The rows on screen, top first, for tests. Only ever a viewport's worth.</summary>
    internal IReadOnlyList<TreeRow> RealizedRows => realized;

    /// <summary>Text layouts held, for tests: bounded by the viewport, not by distance scrolled.</summary>
    internal int CachedLayoutCount => layouts.Count;

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

    private double ContentLeft => ContentPaddingX + GutterWidth;

    private double TextLeft(in TreeRow row) => ContentLeft + row.Depth * IndentWidth + ToggleWidth;

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
        SyncOffset();
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
        SyncOffset();
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
        lastOffsetY = 0;
        selection = null;
        bytesPerRow = InitialBytesPerRow;
        DropLayouts();
        ResetWidestRowWidth();
        Realize();
        SyncOffset();
        InvalidateVisual();
    }

    private void OnDocumentGrew(object? sender, EventArgs e)
    {
        if (anchor is null)
        {
            ResetToTop();
            return;
        }

        Realize();
        RaiseScrollInvalidated(EventArgs.Empty);
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
        if (anchor is null || Bounds.Height <= 0)
            return;

        int needed = (int)Math.Ceiling((Bounds.Height + anchorPixel) / RowHeight);
        var walker = anchor.Clone();
        realized.Add(walker.Current);
        while (realized.Count < needed && walker.MoveNext())
            realized.Add(walker.Current);

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
            var (layout, _) = LayoutFor(row, typeface, FontSize);
            widest = Math.Max(widest, row.Depth * IndentWidth + ToggleWidth + layout.WidthIncludingTrailingWhitespace);
        }

        RecordRowWidth(widest);
    }

    private (TextLayout Layout, string Text) LayoutFor(in TreeRow row, Typeface typeface, double fontSize)
    {
        var key = (row.Node.ValueStart, row.Shape, row.IsExpanded);
        if (layouts.TryGetValue(key, out var cached))
            return cached;

        var foreground = Foreground ?? Brushes.Black;
        runs.Clear();
        document!.Painter.AppendRuns(row, runs);

        var text = new StringBuilder();
        var overrides = new List<ValueSpan<TextRunProperties>>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
                continue;

            var brush = runBrushes is not null && runBrushes.TryGetValue(run.Style, out var styled) ? styled : foreground;
            overrides.Add(new ValueSpan<TextRunProperties>(text.Length, run.Text.Length,
                new GenericTextRunProperties(typeface, fontSize, foregroundBrush: brush)));
            text.Append(run.Text);
        }

        string content = text.ToString();
        var layout = new TextLayout(content, typeface, fontSize, foreground, TextAlignment.Left, TextWrapping.NoWrap,
            textStyleOverrides: overrides);
        var entry = (layout, content);
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
        expandedArrow = null;
        collapsedArrow = null;
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
        RaiseScrollInvalidated(EventArgs.Empty);
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

        expandedArrow ??= new TextLayout(ExpandedGlyph, typeface, fontSize, gutterStyle.Brush);
        collapsedArrow ??= new TextLayout(CollapsedGlyph, typeface, fontSize, gutterStyle.Brush);

        for (int i = 0; i < realized.Count; i++)
        {
            var row = realized[i];
            double y = i * RowHeight - anchorPixel;

            if (SelectionBrush is { } selectionBrush && row.Key == selectedKey)
                context.FillRectangle(selectionBrush, new Rect(0, y, Bounds.Width, RowHeight));

            double gutterX = ContentPaddingX;
            foreach (var gutter in document.Gutters)
            {
                gutter.Draw(context, row, new Rect(gutterX, y, gutter.Width, RowHeight), gutterStyle);
                gutterX += gutter.Width;
            }

            using (context.PushClip(new Rect(contentLeft, y, contentWidth, RowHeight)))
            {
                double indentX = contentLeft + row.Depth * IndentWidth - PanOffset;
                if (row.Shape == TreeRowShape.Open)
                {
                    var arrow = row.IsExpanded ? expandedArrow : collapsedArrow;
                    arrow.Draw(context, new Point(indentX + (ToggleWidth - arrow.WidthIncludingTrailingWhitespace) / 2, CentreInRow(arrow, y)));
                }

                var (layout, text) = LayoutFor(row, typeface, fontSize);
                double textTop = CentreInRow(layout, y);
                double textX = indentX + ToggleWidth;
                DrawHighlights(context, layout, text, textX, textTop);
                layout.Draw(context, new Point(textX, textTop));
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
    /// The document's bytes at the bytes a row covers on screen - but never less than where the
    /// anchor already is plus a viewport, so the host cannot clamp the current offset against an
    /// estimate that has fallen short of it.
    /// </summary>
    protected override double ExtentHeight
    {
        get
        {
            if (anchor is null || document is null)
                return 0;

            double estimated = document.AvailableLength / bytesPerRow * RowHeight;
            return Math.Max(estimated, AnchorPosition() + Bounds.Height);
        }
    }

    private double AnchorPosition() => anchor is null ? 0 : anchor.Current.Start / bytesPerRow * RowHeight + anchorPixel;

    /// <summary>More than this is a jump - a thumb drag - rather than a scroll.</summary>
    private double JumpThreshold => Math.Max(3 * Bounds.Height, 10 * RowHeight);

    protected override void OnOffsetChanged()
    {
        if (syncingOffset || anchor is null)
            return;

        double y = Offset.Y;
        double delta = y - lastOffsetY;
        lastOffsetY = y;

        if (Math.Abs(delta) <= JumpThreshold)
        {
            ScrollBy(delta);
            SyncOffset();
            return;
        }

        // A jump seeks to the byte the offset points at and leaves the host's offset alone, so a
        // thumb being dragged is not pulled back under the pointer.
        if (y <= 0)
            anchor.MoveToStart();
        else
            anchor.SeekTo((long)(y / RowHeight * bytesPerRow));
        anchorPixel = 0;
        Realize();
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
                break;
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

    /// <summary>Tells the host where the anchor now is, without that counting as a scroll.</summary>
    private void SyncOffset()
    {
        double position = AnchorPosition();
        syncingOffset = true;
        try
        {
            Offset = new Vector(Offset.X, position);
        }
        finally
        {
            syncingOffset = false;
        }

        lastOffsetY = position;
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    // ---- selection and input --------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (document is null || realized.Count == 0)
            return;

        Focus();
        var point = e.GetPosition(this);
        int index = (int)Math.Floor((point.Y + anchorPixel) / RowHeight);
        if ((uint)index >= (uint)realized.Count)
            return;

        var row = realized[index];
        Select(row);

        double arrowLeft = ContentLeft + row.Depth * IndentWidth - PanOffset;
        bool onArrow = point.X >= arrowLeft && point.X < arrowLeft + ToggleWidth;
        if (row.Shape == TreeRowShape.Open && (onArrow || e.ClickCount == 2))
            Toggle(row);

        e.Handled = true;
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
                Toggle(current);
                break;
            case Key.Right when current is { Shape: TreeRowShape.Open }:
                if (selection.MoveNext())
                    AfterSelectionMoved(downward: true);
                break;
            case Key.Left when current is { Shape: TreeRowShape.Open, IsExpanded: true }:
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
                parent = ancestor;
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
            SyncOffset();
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreeSurfaceAutomationPeer(this);
}
