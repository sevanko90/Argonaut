using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Ui.Clipboard;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;

namespace Argonaut.Features.Hex;

/// <summary>The bytes a <see cref="HexSurface"/> draws: how many there are now, and one row's
/// worth on request. Read on the UI thread as rows come on screen.</summary>
public interface IHexDocument
{
    /// <summary>How many bytes there are now - read each time, never kept, as a streamed
    /// document grows.</summary>
    long Length { get; }

    /// <summary>Copies row <paramref name="row"/>'s bytes into <paramref name="into"/> (16 long),
    /// returning how many there were: 16, fewer on the last row, 0 past the end or once the
    /// document has been let go.</summary>
    int ReadRow(long row, Span<byte> into);

    /// <summary>The caret and selection, which the surface draws and moves.</summary>
    HexCursor Cursor { get; }

    /// <summary>The selected bytes, for a copy - false when nothing is selected or there is too
    /// much (<see cref="HexCopy.MaxBytes"/>).</summary>
    bool TryReadSelection(out byte[] bytes);
}

/// <summary>
/// Draws a document as a hex dump in three panels - a pinned offset gutter, the sixteen bytes in
/// two groups of eight, and their characters - under a pinned column header, one fixed-height row
/// at a time, reading only the rows on screen. The row count is arithmetic on the length
/// (<see cref="HexRows"/>), so the scroll model is <see cref="RowSurface"/>'s exact one and a
/// multi-gigabyte document scrolls as a small one does.
///
/// The offset gutter and the character panel sit on <see cref="PanelBackground"/>, the bytes on the
/// surface's own. Bytes are coloured by class: zero faint (most of a binary file's padding),
/// printable ASCII as text, everything else in <see cref="OtherByteForeground"/>. The character
/// panel spaces its characters a little wider than the font does, so a mark round one clears its
/// neighbours. Layouts are kept for the rows on screen only.
///
/// The caret and the selection (<see cref="HexCursor"/>) are drawn in both panels at once: the
/// selection behind the bytes, the caret in <see cref="ByteMarkBrush"/> - a solid block with the
/// byte redrawn on it in <see cref="ByteMarkForeground"/>, readable over any byte's colour, in the
/// panel the keys act in, blinking to its outline while the view has focus; the outline alone in
/// the other panel. A click or drag in either panel moves them and makes that panel the one the
/// keys and a copy act in. The byte under the pointer is marked softer, so the caret stays the
/// focus: a pale block of the same accent with a faint edge, behind its text, over a faint band
/// along its row.
/// </summary>
public sealed class HexSurface : RowSurface
{
    public static readonly StyledProperty<IBrush?> OtherByteForegroundProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(OtherByteForeground));

    /// <summary>Brush for zero bytes - fainter than the offsets, as most are padding.</summary>
    public static readonly StyledProperty<IBrush?> ZeroByteForegroundProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(ZeroByteForeground));

    /// <summary>Behind the gutter, the header and the character panel.</summary>
    public static readonly StyledProperty<IBrush?> PanelBackgroundProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(PanelBackground));

    /// <summary>The lines between panels.</summary>
    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(DividerBrush));

    /// <summary>The line under the header - stronger than the panel dividers, so the header
    /// reads as one; the dividers' brush when unset.</summary>
    public static readonly StyledProperty<IBrush?> HeaderLineBrushProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(HeaderLineBrush));

    /// <summary>The accent the caret and the pointer's mark are drawn in: the caret solid, the
    /// pointer's mark a pale tint of it.</summary>
    public static readonly StyledProperty<IBrush?> ByteMarkBrushProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(ByteMarkBrush));

    /// <summary>The caret's byte, drawn again on its solid block.</summary>
    public static readonly StyledProperty<IBrush?> ByteMarkForegroundProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(ByteMarkForeground));

    /// <summary>The faint band along the row under the pointer.</summary>
    public static readonly StyledProperty<IBrush?> RowMarkBrushProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(RowMarkBrush));

    /// <summary>How many bytes the hex panel draws together (<see cref="HexRows.GroupSizes"/>).
    /// Display only: the bytes stay in file order.</summary>
    public static readonly StyledProperty<int> GroupSizeProperty =
        AvaloniaProperty.Register<HexSurface, int>(nameof(GroupSize), 1, coerce: (_, size) => HexRows.IsGroupSize(size) ? size : 1);

    /// <summary>Bytes marked as a hovered reading in the side panel covers them, in both panels.</summary>
    public static readonly StyledProperty<ByteRange?> HighlightRangeProperty =
        AvaloniaProperty.Register<HexSurface, ByteRange?>(nameof(HighlightRange));

    /// <summary>Whether the character panel reads its bytes as UTF-8 (<see cref="HexUtf8"/>)
    /// rather than as ASCII.</summary>
    public static readonly StyledProperty<bool> ReadsUtf8Property =
        AvaloniaProperty.Register<HexSurface, bool>(nameof(ReadsUtf8));

    /// <summary>Space either side of a panel's text.</summary>
    private const double PanelPadding = 14;

    /// <summary>Space either side of the character panel's text - less than the other panels',
    /// as its widened cells already leave room at each end.</summary>
    private const double CharacterPanelPadding = 8;

    private const double HeaderHeight = RowHeight + 2;

    /// <summary>Space between the header's line and the first row, so the header reads as a
    /// header rather than as one more row.</summary>
    private const double HeaderGap = 6;

    private const double RowsTop = HeaderHeight + HeaderGap;

    /// <summary>The hex text's width in characters at the current grouping.</summary>
    private int HexTextChars => HexRows.HexWidth(GroupSize);

    /// <summary>Extra room between the character panel's characters, so a mark around one
    /// clears its neighbours; each glyph sits in the middle of its widened cell.</summary>
    private const double CharacterSpacing = 2;

    private readonly Dictionary<long, RowLayouts> layouts = new();
    private readonly List<long> leaving = new();
    private readonly byte[] rowBytes = new byte[HexRows.BytesPerRow];
    private IHexDocument? document;
    private int offsetDigits = HexRows.MinOffsetDigits;
    private char[] line = new char[HexRows.LineLength(HexRows.MinOffsetDigits)];
    private double scrollTop;
    private long? hovered;
    private bool hoveredInCharacters;
    private HexCursor? cursor;

    // Which panel the keys and a copy act in, and the byte a drag started on (null when no
    // drag is under way).
    private bool keysInCharacters;
    private long? dragFrom;
    private bool dragging;

    // Geometry in the current font, measured when the document or font changes, never while
    // drawing: the width of a character, and how wide each panel is.
    private double charWidth = 8;
    private double gutterWidth;
    private double hexPanelWidth;
    private double characterPanelWidth;

    private TextLayout? characterHeader;
    private TextLayout? offsetHeader;
    private Pen? headerPen;
    private Pen? dividerPen;
    private Pen? groupPen;
    private Pen? caretPen;
    private IBrush? pointerFill;
    private IBrush? characterBand;
    private Pen? pointerEdge;

    // The caret blinks between its solid mark and its outline while the view has focus, and
    // shows solid the moment it moves.
    private DispatcherTimer? caretBlink;
    private bool caretBlinkOff;

    // Run properties per byte class, built for the current font and brushes and dropped with
    // the layouts when either changes.
    private TextRunProperties? zeroRun;
    private TextRunProperties? otherRun;

    /// <summary>A row on screen: its offset and characters laid out, and its bytes, which the
    /// hex panel draws one by one from <see cref="byteGlyphs"/>.</summary>
    private sealed record RowLayouts(TextLayout Offset, TextLayout Characters, byte[] Bytes, int Count, HexCell[]? Cells) : IDisposable
    {
        public void Dispose()
        {
            Offset.Dispose();
            Characters.Dispose();
        }
    }

    // A byte's two hex digits, laid out once for each value in its class's colour, and placed
    // wherever the grouping puts it - so a row's hex costs no text of its own, and the gaps
    // between a group's bytes are arithmetic.
    private readonly TextLayout?[] byteGlyphs = new TextLayout?[256];

    // A UTF-8 cell's glyph in its colour, laid out the first time it is drawn; dropped when it
    // grows past a screen's worth of distinct characters many times over.
    private readonly Dictionary<(int Character, CellInk Ink), TextLayout> utf8Glyphs = new();
    private const int MaxUtf8Glyphs = 4096;

    /// <summary>The colours a UTF-8 cell can take.</summary>
    private enum CellInk : byte
    {
        Text,
        Zero,
        Other,
    }

    // "00".."0F" over the hex panel, laid out once and placed over each group.
    private readonly TextLayout?[] columnLabels = new TextLayout?[HexRows.BytesPerRow];

    static HexSurface()
    {
        AffectsRender<HexSurface>(OtherByteForegroundProperty, ZeroByteForegroundProperty, PanelBackgroundProperty, DividerBrushProperty,
            ByteMarkBrushProperty, ByteMarkForegroundProperty, RowMarkBrushProperty, HeaderLineBrushProperty, HighlightRangeProperty);
    }

    public HexSurface()
    {
        Focusable = true;
        MeasureGeometry();
        ContextMenu = BuildCopyMenu();
    }

    /// <summary>Whether the keys and a copy act in the character panel rather than the hex one.</summary>
    public bool KeysInCharacters => this.keysInCharacters;

    /// <summary>Brush for bytes that are neither zero nor printable ASCII.</summary>
    public IBrush? OtherByteForeground
    {
        get => GetValue(OtherByteForegroundProperty);
        set => SetValue(OtherByteForegroundProperty, value);
    }

    public IBrush? ZeroByteForeground
    {
        get => GetValue(ZeroByteForegroundProperty);
        set => SetValue(ZeroByteForegroundProperty, value);
    }

    public IBrush? PanelBackground
    {
        get => GetValue(PanelBackgroundProperty);
        set => SetValue(PanelBackgroundProperty, value);
    }

    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    public IBrush? HeaderLineBrush
    {
        get => GetValue(HeaderLineBrushProperty);
        set => SetValue(HeaderLineBrushProperty, value);
    }

    public IBrush? ByteMarkBrush
    {
        get => GetValue(ByteMarkBrushProperty);
        set => SetValue(ByteMarkBrushProperty, value);
    }

    public IBrush? ByteMarkForeground
    {
        get => GetValue(ByteMarkForegroundProperty);
        set => SetValue(ByteMarkForegroundProperty, value);
    }

    public ByteRange? HighlightRange
    {
        get => GetValue(HighlightRangeProperty);
        set => SetValue(HighlightRangeProperty, value);
    }

    public bool ReadsUtf8
    {
        get => GetValue(ReadsUtf8Property);
        set => SetValue(ReadsUtf8Property, value);
    }

    public int GroupSize
    {
        get => GetValue(GroupSizeProperty);
        set => SetValue(GroupSizeProperty, value);
    }

    public IBrush? RowMarkBrush
    {
        get => GetValue(RowMarkBrushProperty);
        set => SetValue(RowMarkBrushProperty, value);
    }

    /// <summary>The bytes drawn. Setting it starts at the top and sizes the offset column for the
    /// document's length.</summary>
    public IHexDocument? Document
    {
        get => this.document;
        set
        {
            if (this.cursor is { } previous)
            {
                previous.Moved -= OnCaretMoved;
                previous.RevealRequested -= OnRevealRequested;
            }

            this.document = value;
            this.cursor = value?.Cursor;
            if (this.cursor is { } next)
            {
                next.Moved += OnCaretMoved;
                next.RevealRequested += OnRevealRequested;
            }

            this.dragFrom = null;
            this.offsetDigits = HexRows.OffsetDigits(value?.Length ?? 0);
            this.line = new char[HexRows.LineLength(this.offsetDigits)];
            this.scrollTop = 0;
            this.hovered = null;
            DropLayouts();
            MeasureGeometry();
            InvalidateVisual();
            NotifyScrollPosition();
            ReportViewport();
            TakeReveal();
        }
    }

    /// <summary>The first row on screen.</summary>
    public long TopRow => (long)(this.scrollTop / RowHeight);

    /// <summary>The offset of the byte under the pointer, or null.</summary>
    public long? HoveredOffset => this.hovered;

    /// <summary>Layouts held, for tests: a screen's worth at most.</summary>
    internal int CachedLayoutCount => this.layouts.Count;

    /// <summary>Row <paramref name="row"/>'s character text as drawn, for tests.</summary>
    internal TextLayout LaidOutCharacters(long row) => LayoutFor(row, new Typeface(FontFamily)).Characters;

    /// <summary>The digits drawn for a byte of <paramref name="value"/>, for tests.</summary>
    internal TextLayout LaidOutByte(byte value) => ByteGlyphs(value, new Typeface(FontFamily));

    /// <summary>The hex panel's width, for tests.</summary>
    internal double HexPanelWidth => this.hexPanelWidth;

    /// <summary>The middle of the byte at <paramref name="column"/> on screen row
    /// <paramref name="screenRow"/>, in the hex panel or the character panel - where a pointer
    /// would be over it, for tests.</summary>
    internal Point CentreOfByte(int column, bool inCharacters, int screenRow = 0)
    {
        double y = RowsTop + (screenRow + 0.5) * RowHeight;
        return inCharacters
            ? new Point(CharacterCellLeft(column) + CharacterCell / 2, y)
            : new Point(HexByteX(column) + this.charWidth, y);
    }

    private long RowCount => HexRows.RowCount(this.document?.Length ?? 0);

    private double RowsHeight => Math.Max(0, Bounds.Height - RowsTop);

    // Live, never cached: a growing document's extent must not be clamped against a stale one.
    private double ExtentHeight => RowCount * RowHeight;

    private double MaxScrollTop => Math.Max(0, ExtentHeight - RowsHeight);

    private double CharacterPanelLeft => this.gutterWidth + this.hexPanelWidth;

    private double HexTextLeft => this.gutterWidth + PanelPadding - PanOffset;

    private double CharacterTextLeft => CharacterPanelLeft + CharacterPanelPadding - PanOffset;

    /// <summary>One character's cell in the character panel: its advance and the spacing.</summary>
    private double CharacterCell => this.charWidth + CharacterSpacing;

    /// <summary>Where column <paramref name="column"/>'s cell starts in the character panel.</summary>
    private double CharacterCellLeft(int column) => CharacterTextLeft + column * CharacterCell;

    /// <summary>Where byte <paramref name="column"/>'s digits start in the hex panel's text.</summary>
    private int HexByteColumn(int column) => HexRows.HexPosition(0, column, GroupSize) - HexRows.HexColumn(0);

    /// <summary>Extra room between the bytes inside a group, so each byte's digits stay apart
    /// without the full space a group ends with.</summary>
    private const double ByteGap = 2;

    /// <summary>How many of the gaps inside groups come before byte <paramref name="column"/>.</summary>
    private int InnerGapsBefore(int column) => column - column / GroupSize;

    /// <summary>Where byte <paramref name="column"/>'s digits start on screen: its place in the
    /// row's text, and the gaps inside groups before it.</summary>
    private double HexByteX(int column) => HexTextLeft + HexByteColumn(column) * this.charWidth + InnerGapsBefore(column) * ByteGap;

    /// <summary>How many bytes one step moves in a panel: a group in the hex panel, a byte in the
    /// character panel.</summary>
    private int StepIn(bool inCharacters) => inCharacters ? 1 : GroupSize;

    /// <summary>The columns of row <paramref name="row"/> a mark on <paramref name="column"/>
    /// covers: its group, cut off where the document ends, or the byte alone.</summary>
    private (int First, int Last) MarkSpan(long row, int column, bool wholeGroup)
    {
        if (!wholeGroup || GroupSize == 1)
            return (column, column);

        int first = column - column % GroupSize;
        long available = (this.document?.Length ?? 0) - HexRows.StartOf(row);
        int last = (int)Math.Min(first + GroupSize - 1, Math.Max(first, available - 1));
        return (first, last);
    }

    public override double ScrollFraction => ExtentHeight <= 0 ? 0 : this.scrollTop / ExtentHeight;

    public override double ViewportFraction => ExtentHeight <= 0 ? 1 : Math.Min(1, RowsHeight / ExtentHeight);

    public override bool ShowsEnd => this.scrollTop >= MaxScrollTop - 0.5;

    /// <summary>The panels pan; the gutter stays.</summary>
    public override double PanViewportWidth => Math.Max(0, Bounds.Width - this.gutterWidth);

    public override void ScrollByPixels(double delta) => SetVerticalOffset(this.scrollTop + delta);

    public override void ScrollToFraction(double fraction) => SetVerticalOffset(Math.Clamp(fraction, 0, 1) * ExtentHeight);

    public override void ScrollToEnd() => SetVerticalOffset(MaxScrollTop);

    /// <summary>
    /// More of a streamed document has arrived: the last rows laid out may have been partial, or
    /// read their characters without the bytes that now follow, so they are laid out again, and the
    /// extent the scrollbar shows grows with the document. Called from the UI thread, outside
    /// the render pass.
    /// </summary>
    public void ShowGrowth()
    {
        foreach (var (row, laid) in this.layouts)
        {
            // A partial row, and the row before it, whose characters could not look ahead.
            if (laid.Count < HexRows.BytesPerRow)
            {
                this.leaving.Add(row);
                if (row > 0 && this.layouts.ContainsKey(row - 1) && !this.leaving.Contains(row - 1))
                    this.leaving.Add(row - 1);
            }
        }

        foreach (long row in this.leaving)
        {
            this.layouts[row].Dispose();
            this.layouts.Remove(row);
        }

        this.leaving.Clear();
        ReportViewport();
        NotifyScrollPosition();
        InvalidateVisual();
    }

    /// <summary>Puts row <paramref name="row"/> at the top, or as near as the end allows.</summary>
    public void ScrollToRow(long row) => SetVerticalOffset(row * RowHeight);

    private void SetVerticalOffset(double y)
    {
        double clamped = Math.Clamp(y, 0, MaxScrollTop);
        if (clamped != this.scrollTop)
        {
            this.scrollTop = clamped;
            SetHovered(null);
            InvalidateVisual();
        }

        NotifyScrollPosition();
        ReportViewport();
    }

    protected override void OnViewportChanged()
    {
        SetVerticalOffset(this.scrollTop);
        TakeReveal();
    }

    /// <summary>Tells the cursor which rows are on screen, so a view switch after scrolling the
    /// caret away lands where the user is looking.</summary>
    private void ReportViewport()
    {
        if (this.cursor is not { } at)
            return;

        at.ViewportRows = RowsHeight > 0 && RowCount > 0
            ? (TopRow, Math.Min(RowCount - 1, (long)((this.scrollTop + RowsHeight - 1) / RowHeight)))
            : null;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        // A caret that has just moved shows solid, and starts its blink again from there.
        this.caretBlinkOff = false;
        if (this.caretBlink is { IsEnabled: true } blink)
        {
            blink.Stop();
            blink.Start();
        }

        InvalidateVisual();
    }

    private void OnRevealRequested(object? sender, EventArgs e) => TakeReveal();

    /// <summary>Brings a revealed caret on screen, a third of the way down so what comes after it
    /// shows too - once there is a size to scroll in; until then the reveal waits.</summary>
    private void TakeReveal()
    {
        if (RowsHeight <= 0 || this.cursor is not { } at || !at.TakeReveal() || at.Caret is not { } caret)
            return;

        double top = HexRows.RowOf(caret.Byte) * RowHeight;
        if (top < this.scrollTop || top + RowHeight > this.scrollTop + RowsHeight)
            SetVerticalOffset(top - RowsHeight / 3);
    }

    /// <summary>Scrolls as little as keeps the caret's row on screen.</summary>
    private void ScrollCaretIntoView()
    {
        if (this.cursor?.Caret is not { } caret)
            return;

        double top = HexRows.RowOf(caret.Byte) * RowHeight;
        if (top < this.scrollTop)
            SetVerticalOffset(top);
        else if (top + RowHeight > this.scrollTop + RowsHeight)
            SetVerticalOffset(top + RowHeight - RowsHeight);
    }

    protected override void OnTextStyleChanged()
    {
        DropLayouts();
        MeasureGeometry();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ReadsUtf8Property)
        {
            DropLayouts();
            InvalidateVisual();
            return;
        }

        if (change.Property == GroupSizeProperty)
        {
            // Every row's text and the panel's width change with the grouping; a property set
            // from the toolbar, never while drawing, so the pan scrollbar can be resized here.
            DropLayouts();
            MeasureGeometry();
            InvalidateVisual();
            return;
        }

        // A layout bakes its brushes in, so a theme change re-lays the rows on screen.
        if (change.Property == ForegroundProperty || change.Property == GutterForegroundProperty
            || change.Property == OtherByteForegroundProperty || change.Property == ZeroByteForegroundProperty)
        {
            this.characterBand = null;
            DropLayouts();
            MeasureGeometry();
            InvalidateVisual();
        }
        else if (change.Property == DividerBrushProperty || change.Property == HeaderLineBrushProperty)
        {
            this.dividerPen = null;
            this.groupPen = null;
            this.headerPen = null;
        }
        else if (change.Property == ByteMarkBrushProperty)
        {
            this.caretPen = null;
            this.pointerFill = null;
            this.pointerEdge = null;
        }
    }

    // ---- pointer and keys ---------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        if (this.cursor is not { } at || ByteAt(point.Position) is not { } hit)
            return;

        this.keysInCharacters = hit.InCharacters;
        if (point.Properties.IsRightButtonPressed)
        {
            // A right-click inside the selection copies it; elsewhere it moves the caret first,
            // as a click would.
            if (at.Caret is not { } caret || hit.Offset < caret.Start || hit.Offset >= caret.End)
                at.Place(HexRows.GroupStart(hit.Offset, StepIn(hit.InCharacters)));
            InvalidateVisual();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            at.ExtendTo(hit.Offset, StepIn(hit.InCharacters));
        }
        else
        {
            // A double-click lets a kept width go - the caret alone, on the byte clicked - and,
            // like a click, can still be dragged from.
            long start = HexRows.GroupStart(hit.Offset, StepIn(hit.InCharacters));
            if (e.ClickCount == 2)
                at.Select(HexCaret.At(start));
            else
                at.Place(start);
            this.dragFrom = hit.Offset;
            this.dragging = false;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        SetHovered(ByteAt(position));

        if (this.dragFrom is { } from && this.cursor is { } at)
        {
            // Past the top or bottom, the rows come to the pointer a row at a time.
            if (position.Y < RowsTop)
                ScrollByPixels(-RowHeight);
            else if (position.Y > Bounds.Height)
                ScrollByPixels(RowHeight);

            // A press only becomes a drag once the pointer leaves its byte: a hand that shifts
            // within the byte it clicked keeps the width the click selected.
            if (DragOffsetAt(position) is { } to && (this.dragging || to != from))
            {
                this.dragging = true;
                at.Select(HexCaret.SpanningGroups(from, to, StepIn(this.keysInCharacters)));
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (this.dragFrom is not null)
        {
            this.dragFrom = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        this.dragFrom = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHovered(null);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        this.caretBlinkOff = false;
        this.caretBlink ??= new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
        {
            this.caretBlinkOff = !this.caretBlinkOff;
            InvalidateVisual();
        });
        this.caretBlink.Start();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        this.caretBlink?.Stop();
        this.caretBlinkOff = false;
        InvalidateVisual();
    }

    /// <summary>Escape clears a selection, so it is claimed while there is one.</summary>
    public override bool ClaimsEscape => this.cursor?.Caret is { IsEmpty: false };

    /// <summary>Raised when <see cref="HoveredOffset"/> changes - the pointer moved to another
    /// byte, off the bytes, or the view scrolled under it.</summary>
    public event EventHandler? HoveredOffsetChanged;

    /// <summary>Marks the byte at <paramref name="point"/>, as the pointer does - for tests.</summary>
    internal void HoverAt(Point point) => SetHovered(ByteAt(point));

    /// <summary>Clicks at <paramref name="point"/>: moves the caret there, or with
    /// <paramref name="extend"/> takes the selection there - for tests.</summary>
    internal void ClickAt(Point point, bool extend = false)
    {
        if (this.cursor is not { } at || ByteAt(point) is not { } hit)
            return;

        this.keysInCharacters = hit.InCharacters;
        if (extend)
            at.ExtendTo(hit.Offset, StepIn(hit.InCharacters));
        else
            at.Place(HexRows.GroupStart(hit.Offset, StepIn(hit.InCharacters)));
    }

    /// <summary>Drags from <paramref name="from"/> to <paramref name="to"/> - for tests.</summary>
    internal void DragBetween(Point from, Point to)
    {
        if (this.cursor is not { } at || ByteAt(from) is not { } start)
            return;

        this.keysInCharacters = start.InCharacters;
        if (DragOffsetAt(to) is { } end)
            at.Select(HexCaret.SpanningGroups(start.Offset, end, StepIn(start.InCharacters)));
    }

    private void SetHovered((long Offset, bool InCharacters)? hit)
    {
        long? offset = hit?.Offset;
        bool inCharacters = hit?.InCharacters ?? false;
        if (offset == this.hovered && inCharacters == this.hoveredInCharacters)
            return;

        // The panel only changes what a kept width's mark covers; the byte is what is reported.
        bool moved = offset != this.hovered;
        this.hovered = offset;
        this.hoveredInCharacters = inCharacters;
        InvalidateVisual();
        if (moved)
            HoveredOffsetChanged?.Invoke(this, EventArgs.Empty);
    }

    private long? OffsetAt(Point point) => ByteAt(point)?.Offset;

    /// <summary>The byte under <paramref name="point"/> and which panel it is in, or null over the
    /// gutter, the header, the gap between the groups or past the end.</summary>
    private (long Offset, bool InCharacters)? ByteAt(Point point)
    {
        if (this.document is null || point.Y < RowsTop || point.X < this.gutterWidth)
            return null;

        long row = (long)((point.Y - RowsTop + this.scrollTop) / RowHeight);
        if (ColumnAt(point.X) is not { } column)
            return null;

        long offset = HexRows.StartOf(row) + column.Index;
        return offset < this.document.Length ? (offset, column.InCharacters) : null;
    }

    /// <summary>The byte a drag has reached: the nearest one to <paramref name="point"/> in the
    /// panel the drag is in, so a pointer off the rows' ends or past the panel still selects up to
    /// a byte.</summary>
    private long? DragOffsetAt(Point point)
    {
        long length = this.document?.Length ?? 0;
        if (length <= 0)
            return null;

        double y = Math.Clamp(point.Y, RowsTop, Math.Max(RowsTop, Bounds.Height - 1));
        long row = (long)((y - RowsTop + this.scrollTop) / RowHeight);
        int column = this.keysInCharacters
            ? (int)Math.Floor((point.X - CharacterTextLeft) / CharacterCell)
            : HexColumnNear(point.X);
        column = Math.Clamp(column, 0, HexRows.BytesPerRow - 1);
        return Math.Clamp(HexRows.StartOf(row) + column, 0, length - 1);
    }

    /// <summary>The hex panel's byte nearest <paramref name="x"/>: the last whose digits start at
    /// or before it, so a gap goes to the byte before it.</summary>
    private int HexColumnNear(double x)
    {
        int column = 0;
        while (column < HexRows.BytesPerRow - 1 && HexByteX(column + 1) <= x)
            column++;
        return column;
    }

    private (int Index, bool InCharacters)? ColumnAt(double x)
    {
        double inCharacters = (x - CharacterTextLeft) / CharacterCell;
        if (inCharacters >= 0 && inCharacters < HexRows.BytesPerRow)
            return ((int)inCharacters, true);

        // A byte's two digits and the space after them belong to it, so the pointer never falls
        // between bytes or groups; only the extra space between the halves and the panel's
        // padding are no byte.
        if (x < HexByteX(0))
            return null;

        int column = HexColumnNear(x);
        double start = HexByteX(column);
        double end = column < HexRows.BytesPerRow - 1
            ? Math.Min(start + 3 * this.charWidth, HexByteX(column + 1))
            : start + 2 * this.charWidth;
        return x < end ? (column, false) : null;
    }

    /// <summary>Rows a Page Up or Down moves: a screen, less one row kept for context.</summary>
    private int PageRows => Math.Max(1, (int)(RowsHeight / RowHeight) - 1);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool extend = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        // Option on a Mac, Alt elsewhere: steps a kept width a whole width at a time.
        bool byWidth = (e.KeyModifiers & KeyModifiers.Alt) != 0;

        HexMove? move = e.Key switch
        {
            Key.Left => HexMove.Left,
            Key.Right => HexMove.Right,
            Key.Up => HexMove.Up,
            Key.Down => HexMove.Down,
            Key.PageUp => HexMove.PageUp,
            Key.PageDown => HexMove.PageDown,
            Key.Home => command ? HexMove.DocumentStart : HexMove.RowStart,
            Key.End => command ? HexMove.DocumentEnd : HexMove.RowEnd,
            _ => null,
        };

        if (move is { } step)
        {
            MoveCaret(step, extend, byWidth);
        }
        else if (e.Key == Key.Escape && this.cursor?.Caret is { IsEmpty: false })
        {
            this.cursor.Clear();
        }
        else if (e.Key == Key.A && command)
        {
            this.cursor?.SelectAll();
        }
        else if (e.Key == Key.C && command)
        {
            _ = CopyAsync(this.keysInCharacters ? HexCopyForm.Text : HexCopyForm.Hex);
        }
        else if (e.Key == Key.Tab && !command)
        {
            // Between the panels, as a hex editor's Tab does; the caret stays on its byte.
            this.keysInCharacters = !this.keysInCharacters;
            InvalidateVisual();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    /// <summary>Moves the caret as a key does and keeps it on screen - for tests too.</summary>
    internal void MoveCaret(HexMove move, bool extend = false, bool byWidth = false)
    {
        this.cursor?.Move(move, extend, PageRows, StepIn(this.keysInCharacters), byWidth);
        ScrollCaretIntoView();
    }

    // ---- copy ---------------------------------------------------------------------------

    private ContextMenu BuildCopyMenu()
    {
        MenuItem Item(string header, HexCopyForm form)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => _ = CopyAsync(form);
            return item;
        }

        return new ContextMenu
        {
            Items =
            {
                Item("Copy as hex", HexCopyForm.Hex),
                Item("Copy as text", HexCopyForm.Text),
                Item("Copy bytes", HexCopyForm.Bytes),
            },
        };
    }

    /// <summary>
    /// Copies the selection in <paramref name="form"/>. Refuses rather than truncates when it is
    /// larger than <see cref="HexCopy.MaxBytes"/>.
    /// </summary>
    private async Task CopyAsync(HexCopyForm form)
    {
        if (this.document is not { } from || this.cursor?.Caret is not { IsEmpty: false } caret)
            return;

        if (!from.TryReadSelection(out byte[] bytes))
        {
            if (caret.Length > HexCopy.MaxBytes)
                ToastService.Show($"Selection is too large to copy ({caret.Length / (1024 * 1024)} MB).");
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        if (form == HexCopyForm.Bytes)
        {
            var item = new DataTransferItem();
            // As binary data, not text: what a hex editor takes, and what this app's own paste
            // opens again byte for byte.
            foreach (string identifier in ClipboardFormats.Binary)
                item.Set(DataFormat.CreateBytesPlatformFormat(identifier), bytes);
            var transfer = new DataTransfer();
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
        }
        else
        {
            await clipboard.SetTextAsync(form == HexCopyForm.Hex ? HexCopy.AsHex(bytes) : ReadsUtf8 ? HexUtf8.AsText(bytes) : HexCopy.AsText(bytes));
        }

        string what = form switch { HexCopyForm.Hex => " as hex", HexCopyForm.Text => " as text", _ => string.Empty };
        ToastService.Show($"Copied {bytes.Length:N0} {(bytes.Length == 1 ? "byte" : "bytes")}{what} to clipboard");
    }

    // ---- drawing ------------------------------------------------------------------------

    protected override void RenderRows(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        this.dividerPen ??= DividerBrush is { } divider ? new Pen(divider, 1) : null;
        this.headerPen ??= (HeaderLineBrush ?? DividerBrush) is { } headerLine ? new Pen(headerLine, 1) : null;
        this.groupPen ??= DividerBrush is { } group ? new Pen(group, 1, dashStyle: new DashStyle([2, 3], 0)) : null;

        DrawPanels(context, width, height);

        if (this.document is not null && RowCount > 0)
        {
            long first = (long)(this.scrollTop / RowHeight);
            long last = Math.Min(RowCount - 1, (long)((this.scrollTop + RowsHeight) / RowHeight));
            DropLayoutsOutside(first, last);
            var typeface = new Typeface(FontFamily);
            using (context.PushClip(new Rect(0, RowsTop, width, RowsHeight)))
            {
                DrawRowMark(context);
                DrawSelection(context, first, last);
                DrawPointerMark(context, first, last);
                if (HighlightRange is { } highlighted)
                    DrawRangeMark(context, first, last, highlighted);

                for (long row = first; row <= last; row++)
                {
                    var laid = LayoutFor(row, typeface);
                    double top = RowsTop + row * RowHeight - this.scrollTop;
                    laid.Offset.Draw(context, new Point(PanelPadding, CentreInRow(laid.Offset, top)));
                    using (context.PushClip(new Rect(this.gutterWidth, top, Math.Max(0, width - this.gutterWidth), RowHeight)))
                    {
                        for (int i = 0; i < laid.Count; i++)
                        {
                            var digits = ByteGlyphs(laid.Bytes[i], typeface);
                            digits.Draw(context, new Point(HexByteX(i), CentreInRow(digits, top)));
                        }

                        if (laid.Cells is { } cells)
                            DrawUtf8Cells(context, laid, cells, top, typeface);
                        else
                            laid.Characters.Draw(context, new Point(CharacterTextLeft + CharacterSpacing / 2, CentreInRow(laid.Characters, top)));
                    }
                }

                DrawCaret(context, first, last, typeface);
            }
        }

        DrawDividers(context, width, height);
        DrawHeader(context);
    }

    /// <summary>The gutter and the character panel on the panel background, the character panel
    /// ending where the characters do.</summary>
    private void DrawPanels(DrawingContext context, double width, double height)
    {
        if (PanelBackground is not { } panel)
            return;

        context.FillRectangle(panel, new Rect(0, 0, this.gutterWidth, height));

        double left = CharacterPanelLeft - PanOffset;
        double right = left + this.characterPanelWidth;
        left = Math.Max(this.gutterWidth, left);
        if (right > left)
            context.FillRectangle(panel, new Rect(left, 0, right - left, height));
    }

    /// <summary>A faint band along the row under the pointer, from its offset to its last
    /// character, behind the text.</summary>
    private void DrawRowMark(DrawingContext context)
    {
        if (this.hovered is not { } offset || RowMarkBrush is not { } band)
            return;

        double top = RowsTop + HexRows.RowOf(offset) * RowHeight - this.scrollTop;
        double right = Math.Max(this.gutterWidth, CharacterPanelLeft + this.characterPanelWidth - PanOffset);
        context.FillRectangle(band, new Rect(0, top, right, RowHeight));
    }

    /// <summary>The selection behind the bytes on screen, in both panels: one band a row in each,
    /// running across the gap between the groups.</summary>
    private void DrawSelection(DrawingContext context, long first, long last)
    {
        if (this.cursor?.Caret is not { IsEmpty: false } caret || SelectionBrush is not { } fill)
            return;

        long from = Math.Max(first, HexRows.RowOf(caret.Start));
        long to = Math.Min(last, HexRows.RowOf(caret.End - 1));
        for (long row = from; row <= to; row++)
        {
            long rowStart = HexRows.StartOf(row);
            int a = (int)(Math.Max(caret.Start, rowStart) - rowStart);
            int b = (int)(Math.Min(caret.End, rowStart + HexRows.BytesPerRow) - rowStart);
            double top = RowsTop + row * RowHeight - this.scrollTop;

            double hexLeft = HexByteX(a) - this.charWidth / 2;
            double hexRight = HexByteX(b - 1) + 2.5 * this.charWidth;
            context.FillRectangle(fill, new Rect(hexLeft, top, hexRight - hexLeft, RowHeight));
            context.FillRectangle(fill, new Rect(CharacterCellLeft(a), top, (b - a) * CharacterCell, RowHeight));
        }
    }

    /// <summary>The cell a mark is drawn in around columns <paramref name="first"/> to
    /// <paramref name="last"/> of a row whose top is <paramref name="top"/>, in the hex panel or
    /// the character panel.</summary>
    private Rect MarkCell((int First, int Last) span, double top, bool inCharacters)
    {
        double cellTop = top + 2;
        double height = RowHeight - 4;
        if (inCharacters)
        {
            double left = CharacterCellLeft(span.First) - 0.5;
            return new Rect(left, cellTop, CharacterCellLeft(span.Last + 1) + 0.5 - left, height);
        }

        double hexLeft = HexByteX(span.First) - 4;
        double hexRight = HexByteX(span.Last) + 2 * this.charWidth + 4;
        return new Rect(hexLeft, cellTop, hexRight - hexLeft, height);
    }

    /// <summary>
    /// The caret, in both panels. In the panel the keys act in it is a solid block with its bytes
    /// drawn again on it in <see cref="ByteMarkForeground"/> - readable over any byte's colour -
    /// blinking to its outline while the view has focus; in the other panel, and in both while the
    /// view does not have focus, it is the outline alone. In the hex panel it covers the caret's
    /// group; in the character panel its byte while the keys move by byte there, else the group's.
    /// </summary>
    private void DrawCaret(DrawingContext context, long first, long last, Typeface typeface)
    {
        if (this.cursor?.Caret is not { } caret || ByteMarkBrush is not { } accent)
            return;

        long row = HexRows.RowOf(caret.Byte);
        if (row < first || row > last)
            return;

        this.caretPen ??= new Pen(accent, 1.5);
        int column = (int)(caret.Byte - HexRows.StartOf(row));
        double top = RowsTop + row * RowHeight - this.scrollTop;

        bool solid = IsFocused && !this.caretBlinkOff;
        foreach (bool inCharacters in (ReadOnlySpan<bool>)[false, true])
        {
            var span = MarkSpan(row, column, wholeGroup: !(inCharacters && this.keysInCharacters));
            if (solid && inCharacters == this.keysInCharacters)
                DrawSolidMark(context, typeface, row, span, top, inCharacters, accent);
            else
                context.DrawRectangle(null, this.caretPen, new RoundedRect(MarkCell(span, top, inCharacters).Deflate(0.75), 4));
        }
    }

    /// <summary>The byte under the pointer, marked in both panels behind its text - its group, when
    /// bytes are grouped: a pale block of the accent with a faint edge, softer than the caret,
    /// which stays the focus, and the bytes keep their own colours. While a width is kept it marks
    /// what a click there would select.</summary>
    private void DrawPointerMark(DrawingContext context, long first, long last)
    {
        if (this.hovered is not { } offset || ByteMarkBrush is not ISolidColorBrush accent)
            return;

        if (this.cursor?.KeptWidth is { } width && this.document is { } document)
        {
            long start = HexRows.GroupStart(offset, StepIn(this.hoveredInCharacters));
            DrawRangeMark(context, first, last, new ByteRange(start, Math.Min(width, document.Length - start)));
            return;
        }

        this.pointerFill ??= new SolidColorBrush(accent.Color, 0.16);
        this.pointerEdge ??= new Pen(new SolidColorBrush(accent.Color, 0.55), 1);

        long row = HexRows.RowOf(offset);
        var span = MarkSpan(row, (int)(offset - HexRows.StartOf(row)), wholeGroup: true);
        double top = RowsTop + row * RowHeight - this.scrollTop;
        foreach (bool inCharacters in (ReadOnlySpan<bool>)[false, true])
            context.DrawRectangle(this.pointerFill, this.pointerEdge, new RoundedRect(MarkCell(span, top, inCharacters).Deflate(0.5), 4));
    }

    /// <summary>Marks <paramref name="range"/> as the pointer's byte is, row by row in both panels:
    /// a range near a row's end runs on into the next. A hovered reading's bytes, or what a click
    /// would select while a width is kept.</summary>
    private void DrawRangeMark(DrawingContext context, long first, long last, ByteRange range)
    {
        if (range.Length <= 0 || ByteMarkBrush is not ISolidColorBrush accent)
            return;

        this.pointerFill ??= new SolidColorBrush(accent.Color, 0.16);
        this.pointerEdge ??= new Pen(new SolidColorBrush(accent.Color, 0.55), 1);

        long from = Math.Max(first, HexRows.RowOf(range.Offset));
        long to = Math.Min(last, HexRows.RowOf(range.End - 1));
        for (long row = from; row <= to; row++)
        {
            long rowStart = HexRows.StartOf(row);
            int a = (int)(Math.Max(range.Offset, rowStart) - rowStart);
            int b = (int)(Math.Min(range.End, rowStart + HexRows.BytesPerRow) - rowStart) - 1;
            double top = RowsTop + row * RowHeight - this.scrollTop;
            foreach (bool inCharacters in (ReadOnlySpan<bool>)[false, true])
                context.DrawRectangle(this.pointerFill, this.pointerEdge, new RoundedRect(MarkCell((a, b), top, inCharacters).Deflate(0.5), 4));
        }
    }

    /// <summary>A solid block behind <paramref name="span"/> of row <paramref name="row"/> in one
    /// panel, with its bytes drawn again on it in <see cref="ByteMarkForeground"/>.</summary>
    private void DrawSolidMark(DrawingContext context, Typeface typeface, long row, (int First, int Last) span, double top, bool inCharacters, IBrush block)
    {
        Span<byte> bytes = stackalloc byte[HexRows.BytesPerRow];
        if (this.document is null || this.document.ReadRow(row, bytes) <= span.Last)
            return;

        context.DrawRectangle(block, null, new RoundedRect(MarkCell(span, top, inCharacters), 4));

        // A group's bytes are drawn together, as the row draws them; characters keep the
        // panel's spacing.
        int count = span.Last - span.First + 1;
        Span<char> text = stackalloc char[count * 2];
        for (int i = 0; i < count; i++)
        {
            byte value = bytes[span.First + i];
            if (inCharacters)
            {
                text[i] = HexRows.ClassOf(value) == HexByteClass.Printable ? (char)value : '.';
            }
            else
            {
                text[i * 2] = "0123456789ABCDEF"[value >> 4];
                text[i * 2 + 1] = "0123456789ABCDEF"[value & 0xF];
            }
        }

        var ink = ByteMarkForeground ?? Background ?? Brushes.White;
        if (inCharacters && ReadsUtf8 && this.layouts.TryGetValue(row, out var laid) && laid.Cells is { } cells)
        {
            for (int i = span.First; i <= span.Last && i < cells.Length; i++)
            {
                using var glyph = Utf8Glyph(cells[i], bytes[i], typeface, ink);
                glyph?.Draw(context, new Point(CharacterCellLeft(i) + CharacterSpacing / 2, CentreInRow(glyph, top)));
            }

            return;
        }

        if (inCharacters)
        {
            using var layout = new TextLayout(new string(text[..count]), typeface, FontSize, ink, letterSpacing: CharacterSpacing);
            layout.Draw(context, new Point(CharacterCellLeft(span.First) + CharacterSpacing / 2, CentreInRow(layout, top)));
            return;
        }

        for (int i = 0; i < count; i++)
        {
            using var digits = new TextLayout(new string(text.Slice(i * 2, 2)), typeface, FontSize, ink);
            digits.Draw(context, new Point(HexByteX(span.First + i), CentreInRow(digits, top)));
        }
    }

    /// <summary>Where the data ends on screen: the right edge of the character panel.</summary>
    private double DataRight => Math.Max(this.gutterWidth, CharacterPanelLeft + this.characterPanelWidth - PanOffset);

    /// <summary>The column header, pinned above the rows and only as wide as the data: "Offset"
    /// over the gutter, each byte's column over the hex panel, and "Text" over the character
    /// panel - whose columns line up with the hex panel's, so numbering them again would only
    /// crowd the header. Semibold, so it reads as labels rather than data; a line and a gap
    /// separate it from the rows.</summary>
    private void DrawHeader(DrawingContext context)
    {
        double right = DataRight;
        if (PanelBackground is { } panel)
            context.FillRectangle(panel, new Rect(0, 0, right, HeaderHeight));

        if (this.characterHeader is null || this.offsetHeader is null)
            return;

        double y = (HeaderHeight - this.offsetHeader.Height) / 2;
        this.offsetHeader.Draw(context, new Point(PanelPadding, y));
        using (context.PushClip(new Rect(this.gutterWidth, 0, Math.Max(0, right - this.gutterWidth), HeaderHeight)))
        {
            // Each group is labelled by its offset in the row: 00 01 02 .. one by one, 00 04 08
            // 0C in fours.
            for (int i = 0; i < HexRows.BytesPerRow; i += GroupSize)
            {
                if (this.columnLabels[i] is { } label)
                    label.Draw(context, new Point(HexByteX(i), y));
            }

            this.characterHeader.Draw(context, new Point(CharacterTextLeft + CharacterSpacing / 2, y));
        }

        if (this.headerPen is { } line)
            context.DrawLine(line, new Point(0, HeaderHeight - 0.5), new Point(right, HeaderHeight - 0.5));
    }

    private void DrawDividers(DrawingContext context, double width, double height)
    {
        if (this.dividerPen is not { } divider)
            return;

        context.DrawLine(divider, new Point(this.gutterWidth - 0.5, 0), new Point(this.gutterWidth - 0.5, height));

        using (context.PushClip(new Rect(this.gutterWidth, 0, Math.Max(0, width - this.gutterWidth), height)))
        {
            double between = CharacterPanelLeft - PanOffset - 0.5;
            context.DrawLine(divider, new Point(between, 0), new Point(between, height));

            // The two groups of eight, split faintly through the rows.
            if (this.groupPen is { } group)
            {
                double gap = HexByteX(HexRows.BytesPerRow / 2) - this.charWidth / 2;
                context.DrawLine(group, new Point(gap, RowsTop), new Point(gap, height - 4));
            }
        }
    }

    private RowLayouts LayoutFor(long row, Typeface typeface)
    {
        if (this.layouts.TryGetValue(row, out var cached))
            return cached;

        int count = this.document!.ReadRow(row, this.rowBytes);
        HexRows.Format(this.rowBytes.AsSpan(0, count), HexRows.StartOf(row), this.offsetDigits, this.line, GroupSize);

        var foreground = Foreground ?? Brushes.Black;
        int characterColumn = HexRows.CharacterColumn(this.offsetDigits, GroupSize);
        var laid = new RowLayouts(
            new TextLayout(new string(this.line, 0, this.offsetDigits), typeface, FontSize, GutterForeground ?? foreground),
            new TextLayout(new string(this.line, characterColumn, count), typeface, FontSize, foreground,
                TextAlignment.Left, TextWrapping.NoWrap, letterSpacing: CharacterSpacing,
                textStyleOverrides: CharacterStyles(count, typeface)),
            this.rowBytes[..count],
            count,
            ReadsUtf8 ? Utf8Cells(row, count) : null);
        this.layouts[row] = laid;
        return laid;
    }

    /// <summary>Row <paramref name="row"/>'s cells read as UTF-8, looking up to
    /// <see cref="HexUtf8.Reach"/> bytes into the rows either side for characters that cross.</summary>
    private HexCell[] Utf8Cells(long row, int count)
    {
        Span<byte> neighbour = stackalloc byte[HexRows.BytesPerRow];
        Span<byte> window = stackalloc byte[HexUtf8.Reach + HexRows.BytesPerRow + HexUtf8.Reach];
        int before = 0;
        if (row > 0)
        {
            int previous = this.document!.ReadRow(row - 1, neighbour);
            before = Math.Min(HexUtf8.Reach, previous);
            neighbour.Slice(previous - before, before).CopyTo(window);
        }

        this.rowBytes.AsSpan(0, count).CopyTo(window[before..]);
        int after = count == HexRows.BytesPerRow ? Math.Min(HexUtf8.Reach, this.document!.ReadRow(row + 1, neighbour)) : 0;
        neighbour[..after].CopyTo(window[(before + count)..]);

        var cells = new HexCell[count];
        HexUtf8.ReadRow(window[..(before + count + after)], before, count, cells);
        return cells;
    }

    /// <summary>A row's character panel read as UTF-8, cell by cell: each character at its first
    /// byte's cell, spreading over its other bytes', clipped where the row ends.</summary>
    private void DrawUtf8Cells(DrawingContext context, RowLayouts laid, HexCell[] cells, double top, Typeface typeface)
    {
        double left = CharacterCellLeft(0);
        using (context.PushClip(new Rect(left, top, CharacterCellLeft(HexRows.BytesPerRow) - left, RowHeight)))
        {
            DrawCharacterBands(context, cells, top);
            for (int i = 0; i < cells.Length; i++)
            {
                if (Utf8Glyph(cells[i], laid.Bytes[i], typeface, ink: null) is { } glyph)
                    glyph.Draw(context, new Point(CharacterCellLeft(i) + CharacterSpacing / 2, CentreInRow(glyph, top)));
            }
        }
    }

    /// <summary>
    /// A faint band behind every cell of a multi-byte character, its first byte's included, so its
    /// other bytes' cells read as part of it rather than as spaces. A character begun on the row
    /// above, or running on into the next, has its band open at that edge. The band is the text's
    /// own colour, faint: the view's colours are kept for what they mean.
    /// </summary>
    private void DrawCharacterBands(DrawingContext context, HexCell[] cells, double top)
    {
        if ((Foreground as ISolidColorBrush)?.Color is not { } ink)
            return;

        this.characterBand ??= new SolidColorBrush(ink, 0.09);
        double cellTop = top + 2;
        double height = RowHeight - 4;

        int carried = 0;
        while (carried < cells.Length && cells[carried].Kind == HexCellKind.CarriedOver)
            carried++;
        if (carried > 0)
            Band(0, carried - 1, openLeft: true, openRight: false);

        for (int i = carried; i < cells.Length; i++)
        {
            if (cells[i].Kind != HexCellKind.Character || cells[i].Length < 2)
                continue;

            int end = i + cells[i].Length - 1;
            Band(i, Math.Min(end, cells.Length - 1), openLeft: false, openRight: end >= cells.Length);
        }

        void Band(int first, int last, bool openLeft, bool openRight)
        {
            double left = CharacterCellLeft(first) + (openLeft ? -4 : 0.5);
            double right = CharacterCellLeft(last + 1) + (openRight ? 4 : -0.5);
            context.DrawRectangle(this.characterBand, null, new RoundedRect(new Rect(left, cellTop, right - left, height), 4));
        }
    }

    /// <summary>What a UTF-8 cell draws, in its colour or in <paramref name="ink"/>; null for a
    /// cell its character's first byte draws over. Laid out once per character and colour.</summary>
    private TextLayout? Utf8Glyph(HexCell cell, byte value, Typeface typeface, IBrush? ink)
    {
        (int character, CellInk kind) = cell.Kind switch
        {
            HexCellKind.Character => (cell.Character.Value, CellInk.Text),
            HexCellKind.CarriedOver => (-1, CellInk.Text),
            HexCellKind.Unreadable => ('.', HexRows.ClassOf(value) == HexByteClass.Zero ? CellInk.Zero : CellInk.Other),
            _ => (-1, CellInk.Text),
        };
        if (character < 0)
            return null;

        string text = cell.Kind == HexCellKind.Character ? cell.Character.ToString() : ".";
        if (ink is not null)
            return new TextLayout(text, typeface, FontSize, ink);

        if (this.utf8Glyphs.TryGetValue((character, kind), out var laid))
            return laid;

        if (this.utf8Glyphs.Count >= MaxUtf8Glyphs)
            DropUtf8Glyphs();

        var brush = kind switch
        {
            CellInk.Zero => ZeroByteForeground ?? GutterForeground,
            CellInk.Other => OtherByteForeground,
            _ => null,
        } ?? Foreground ?? Brushes.Black;
        return this.utf8Glyphs[(character, kind)] = new TextLayout(text, typeface, FontSize, brush);
    }

    private void DropUtf8Glyphs()
    {
        foreach (var glyph in this.utf8Glyphs.Values)
            glyph.Dispose();
        this.utf8Glyphs.Clear();
    }

    /// <summary>A byte's two hex digits in its class's colour, laid out the first time that value
    /// is drawn.</summary>
    private TextLayout ByteGlyphs(byte value, Typeface typeface)
    {
        if (this.byteGlyphs[value] is { } laid)
            return laid;

        var brush = HexRows.ClassOf(value) switch
        {
            HexByteClass.Zero => ZeroByteForeground ?? GutterForeground,
            HexByteClass.Other => OtherByteForeground,
            _ => null,
        } ?? Foreground ?? Brushes.Black;
        return this.byteGlyphs[value] = new TextLayout(value.ToString("X2"), typeface, FontSize, brush);
    }

    /// <summary>The coloured spans of a row's characters for <paramref name="count"/> bytes: each
    /// byte that is not printable ASCII in its class's colour, in order of position, which a
    /// layout requires. Printable bytes take the layout's own foreground.</summary>
    private List<ValueSpan<TextRunProperties>> CharacterStyles(int count, Typeface typeface)
    {
        this.zeroRun ??= Run(typeface, ZeroByteForeground ?? GutterForeground);
        this.otherRun ??= Run(typeface, OtherByteForeground);

        // A layout keeps the list it is given, so each has its own.
        var styles = new List<ValueSpan<TextRunProperties>>(count);
        for (int i = 0; i < count; i++)
        {
            var run = HexRows.ClassOf(this.rowBytes[i]) switch
            {
                HexByteClass.Zero => this.zeroRun,
                HexByteClass.Other => this.otherRun,
                _ => null,
            };

            if (run is not null)
                styles.Add(new(i, 1, run));
        }

        return styles;
    }

    private TextRunProperties Run(Typeface typeface, IBrush? brush)
        => new GenericTextRunProperties(typeface, FontSize, foregroundBrush: brush ?? Foreground ?? Brushes.Black);

    /// <summary>The geometry in the current font - every row is the same width, the font being
    /// monospaced - and the header's text. Measured when the document or font changes, never
    /// while drawing.</summary>
    private void MeasureGeometry()
    {
        var typeface = new Typeface(FontFamily);
        using (var sample = new TextLayout(new string('0', 64), typeface, FontSize, Brushes.Black))
            this.charWidth = sample.WidthIncludingTrailingWhitespace / 64;

        this.gutterWidth = PanelPadding + this.offsetDigits * this.charWidth + PanelPadding;
        this.hexPanelWidth = PanelPadding + HexTextChars * this.charWidth + InnerGapsBefore(HexRows.BytesPerRow - 1) * ByteGap + PanelPadding;
        this.characterPanelWidth = CharacterPanelPadding + HexRows.BytesPerRow * (this.charWidth + CharacterSpacing) + CharacterPanelPadding;

        // Semibold, which a monospaced font draws at the same advance, so the labels stay over
        // their columns.
        var labels = new Typeface(FontFamily, FontStyle.Normal, FontWeight.SemiBold);
        var muted = GutterForeground ?? Foreground ?? Brushes.Gray;
        this.characterHeader?.Dispose();
        this.offsetHeader?.Dispose();
        for (int i = 0; i < HexRows.BytesPerRow; i++)
        {
            this.columnLabels[i]?.Dispose();
            this.columnLabels[i] = new TextLayout(i.ToString("X2"), labels, FontSize, muted);
        }

        this.characterHeader = new TextLayout("Text", labels, FontSize, muted);
        this.offsetHeader = new TextLayout("Offset", labels, FontSize, muted);

        ResetWidestRowWidth(this.hexPanelWidth + this.characterPanelWidth);
    }

    private void DropLayoutsOutside(long first, long last)
    {
        foreach (long row in this.layouts.Keys)
        {
            if (row < first || row > last)
                this.leaving.Add(row);
        }

        foreach (long row in this.leaving)
        {
            this.layouts[row].Dispose();
            this.layouts.Remove(row);
        }

        this.leaving.Clear();
    }

    private void DropLayouts()
    {
        DropUtf8Glyphs();
        for (int i = 0; i < this.byteGlyphs.Length; i++)
        {
            this.byteGlyphs[i]?.Dispose();
            this.byteGlyphs[i] = null;
        }

        foreach (var laid in this.layouts.Values)
            laid.Dispose();
        this.layouts.Clear();
        this.zeroRun = null;
        this.otherRun = null;
    }
}
