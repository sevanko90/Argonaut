using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    /// <summary>The hex text's width in characters: three a byte and one more between the groups,
    /// less the trailing space.</summary>
    private const int HexTextChars = HexRows.BytesPerRow * 3;

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
    private HexCursor? cursor;

    // Which panel the keys and a copy act in, and the byte a drag started on (null when no
    // drag is under way).
    private bool keysInCharacters;
    private long? dragFrom;

    // Geometry in the current font, measured when the document or font changes, never while
    // drawing: the width of a character, and how wide each panel is.
    private double charWidth = 8;
    private double gutterWidth;
    private double hexPanelWidth;
    private double characterPanelWidth;

    private TextLayout? hexHeader;
    private TextLayout? characterHeader;
    private TextLayout? offsetHeader;
    private Pen? headerPen;
    private Pen? dividerPen;
    private Pen? groupPen;
    private Pen? caretPen;
    private IBrush? pointerFill;
    private Pen? pointerEdge;

    // The caret blinks between its solid mark and its outline while the view has focus, and
    // shows solid the moment it moves.
    private DispatcherTimer? caretBlink;
    private bool caretBlinkOff;

    // Run properties per byte class, built for the current font and brushes and dropped with
    // the layouts when either changes.
    private TextRunProperties? zeroRun;
    private TextRunProperties? otherRun;

    private sealed record RowLayouts(TextLayout Offset, TextLayout Hex, TextLayout Characters) : IDisposable
    {
        public void Dispose()
        {
            Offset.Dispose();
            Hex.Dispose();
            Characters.Dispose();
        }
    }

    static HexSurface()
    {
        AffectsRender<HexSurface>(OtherByteForegroundProperty, ZeroByteForegroundProperty, PanelBackgroundProperty, DividerBrushProperty,
            ByteMarkBrushProperty, ByteMarkForegroundProperty, RowMarkBrushProperty, HeaderLineBrushProperty);
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

    /// <summary>Row <paramref name="row"/>'s hex and character text as drawn, for tests.</summary>
    internal (TextLayout Hex, TextLayout Characters) LaidOutRow(long row)
    {
        var laid = LayoutFor(row, new Typeface(FontFamily));
        return (laid.Hex, laid.Characters);
    }

    /// <summary>The middle of the byte at <paramref name="column"/> on screen row
    /// <paramref name="screenRow"/>, in the hex panel or the character panel - where a pointer
    /// would be over it, for tests.</summary>
    internal Point CentreOfByte(int column, bool inCharacters, int screenRow = 0)
    {
        double y = RowsTop + (screenRow + 0.5) * RowHeight;
        return inCharacters
            ? new Point(CharacterCellLeft(column) + CharacterCell / 2, y)
            : new Point(HexTextLeft + (HexByteColumn(column) + 1) * this.charWidth, y);
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
    private static int HexByteColumn(int column) => HexRows.HexPosition(0, column) - HexRows.HexColumn(0);

    public override double ScrollFraction => ExtentHeight <= 0 ? 0 : this.scrollTop / ExtentHeight;

    public override double ViewportFraction => ExtentHeight <= 0 ? 1 : Math.Min(1, RowsHeight / ExtentHeight);

    public override bool ShowsEnd => this.scrollTop >= MaxScrollTop - 0.5;

    /// <summary>The panels pan; the gutter stays.</summary>
    public override double PanViewportWidth => Math.Max(0, Bounds.Width - this.gutterWidth);

    public override void ScrollByPixels(double delta) => SetVerticalOffset(this.scrollTop + delta);

    public override void ScrollToFraction(double fraction) => SetVerticalOffset(Math.Clamp(fraction, 0, 1) * ExtentHeight);

    public override void ScrollToEnd() => SetVerticalOffset(MaxScrollTop);

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

        // A layout bakes its brushes in, so a theme change re-lays the rows on screen.
        if (change.Property == ForegroundProperty || change.Property == GutterForegroundProperty
            || change.Property == OtherByteForegroundProperty || change.Property == ZeroByteForegroundProperty)
        {
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
                at.Place(hit.Offset);
            InvalidateVisual();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            at.ExtendTo(hit.Offset);
        }
        else
        {
            at.Place(hit.Offset);
            this.dragFrom = hit.Offset;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        SetHovered(OffsetAt(position));

        if (this.dragFrom is { } from && this.cursor is { } at)
        {
            // Past the top or bottom, the rows come to the pointer a row at a time.
            if (position.Y < RowsTop)
                ScrollByPixels(-RowHeight);
            else if (position.Y > Bounds.Height)
                ScrollByPixels(RowHeight);

            if (DragOffsetAt(position) is { } to)
                at.Select(HexCaret.Spanning(from, to));
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

    /// <summary>Raised when <see cref="HoveredOffset"/> changes - the pointer moved to another
    /// byte, off the bytes, or the view scrolled under it.</summary>
    public event EventHandler? HoveredOffsetChanged;

    /// <summary>Marks the byte at <paramref name="point"/>, as the pointer does - for tests.</summary>
    internal void HoverAt(Point point) => SetHovered(OffsetAt(point));

    /// <summary>Clicks at <paramref name="point"/>: moves the caret there, or with
    /// <paramref name="extend"/> takes the selection there - for tests.</summary>
    internal void ClickAt(Point point, bool extend = false)
    {
        if (this.cursor is not { } at || ByteAt(point) is not { } hit)
            return;

        this.keysInCharacters = hit.InCharacters;
        if (extend)
            at.ExtendTo(hit.Offset);
        else
            at.Place(hit.Offset);
    }

    /// <summary>Drags from <paramref name="from"/> to <paramref name="to"/> - for tests.</summary>
    internal void DragBetween(Point from, Point to)
    {
        if (this.cursor is not { } at || ByteAt(from) is not { } start)
            return;

        this.keysInCharacters = start.InCharacters;
        if (DragOffsetAt(to) is { } end)
            at.Select(HexCaret.Spanning(start.Offset, end));
    }

    private void SetHovered(long? offset)
    {
        if (offset == this.hovered)
            return;

        this.hovered = offset;
        InvalidateVisual();
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

    /// <summary>The hex panel's byte nearest <paramref name="x"/>, the gap between the groups
    /// going to the byte before it.</summary>
    private int HexColumnNear(double x)
    {
        int index = (int)Math.Floor((x - HexTextLeft) / this.charWidth);
        int half = HexRows.BytesPerRow / 2 * 3;
        return index <= half ? index / 3 : (index - half - 1) / 3 + HexRows.BytesPerRow / 2;
    }

    private (int Index, bool InCharacters)? ColumnAt(double x)
    {
        double inCharacters = (x - CharacterTextLeft) / CharacterCell;
        if (inCharacters >= 0 && inCharacters < HexRows.BytesPerRow)
            return ((int)inCharacters, true);

        // A byte's two digits and the space after them belong to it, so the pointer never falls
        // between bytes; only the gap between the groups and the panel's padding are no byte.
        int index = (int)Math.Floor((x - HexTextLeft) / this.charWidth);
        if (index < 0 || index >= HexTextChars)
            return null;

        int half = HexRows.BytesPerRow / 2 * 3;
        if (index < half)
            return (index / 3, false);

        return index == half ? null : ((index - half - 1) / 3 + HexRows.BytesPerRow / 2, false);
    }

    /// <summary>Rows a Page Up or Down moves: a screen, less one row kept for context.</summary>
    private int PageRows => Math.Max(1, (int)(RowsHeight / RowHeight) - 1);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool extend = (e.KeyModifiers & KeyModifiers.Shift) != 0;

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
            MoveCaret(step, extend);
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
    internal void MoveCaret(HexMove move, bool extend = false)
    {
        this.cursor?.Move(move, extend, PageRows);
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
            await clipboard.SetTextAsync(form == HexCopyForm.Hex ? HexCopy.AsHex(bytes) : HexCopy.AsText(bytes));
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
                DrawPointerMark(context);

                for (long row = first; row <= last; row++)
                {
                    var laid = LayoutFor(row, typeface);
                    double top = RowsTop + row * RowHeight - this.scrollTop;
                    laid.Offset.Draw(context, new Point(PanelPadding, CentreInRow(laid.Offset, top)));
                    using (context.PushClip(new Rect(this.gutterWidth, top, Math.Max(0, width - this.gutterWidth), RowHeight)))
                    {
                        laid.Hex.Draw(context, new Point(HexTextLeft, CentreInRow(laid.Hex, top)));
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

            double hexLeft = HexTextLeft + HexByteColumn(a) * this.charWidth - this.charWidth / 2;
            double hexRight = HexTextLeft + (HexByteColumn(b - 1) + 2) * this.charWidth + this.charWidth / 2;
            context.FillRectangle(fill, new Rect(hexLeft, top, hexRight - hexLeft, RowHeight));
            context.FillRectangle(fill, new Rect(CharacterCellLeft(a), top, (b - a) * CharacterCell, RowHeight));
        }
    }

    /// <summary>The cell a mark is drawn in around byte <paramref name="column"/> of a row whose
    /// top is <paramref name="top"/>, in the hex panel or the character panel.</summary>
    private Rect MarkCell(int column, double top, bool inCharacters)
    {
        double cellTop = top + 2;
        double height = RowHeight - 4;
        if (inCharacters)
            return new Rect(CharacterCellLeft(column) - 0.5, cellTop, CharacterCell + 1, height);

        double hexX = HexTextLeft + HexByteColumn(column) * this.charWidth;
        return new Rect(hexX - 4, cellTop, 2 * this.charWidth + 8, height);
    }

    /// <summary>
    /// The caret, in both panels. In the panel the keys act in it is a solid block with the byte
    /// drawn again on it in <see cref="ByteMarkForeground"/> - readable over any byte's colour -
    /// blinking to its outline while the view has focus; in the other panel, and in both while the
    /// view does not have focus, it is the outline alone.
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
            var cell = MarkCell(column, top, inCharacters);
            if (solid && inCharacters == this.keysInCharacters)
                DrawSolidMark(context, typeface, row, column, top, inCharacters, accent);
            else
                context.DrawRectangle(null, this.caretPen, new RoundedRect(cell.Deflate(0.75), 4));
        }
    }

    /// <summary>The byte under the pointer, marked in both panels behind its text: a pale block
    /// of the accent with a faint edge - softer than the caret, which stays the focus, and the
    /// byte keeps its own colour.</summary>
    private void DrawPointerMark(DrawingContext context)
    {
        if (this.hovered is not { } offset || ByteMarkBrush is not ISolidColorBrush accent)
            return;

        this.pointerFill ??= new SolidColorBrush(accent.Color, 0.16);
        this.pointerEdge ??= new Pen(new SolidColorBrush(accent.Color, 0.55), 1);

        long row = HexRows.RowOf(offset);
        int column = (int)(offset - HexRows.StartOf(row));
        double top = RowsTop + row * RowHeight - this.scrollTop;
        foreach (bool inCharacters in (ReadOnlySpan<bool>)[false, true])
            context.DrawRectangle(this.pointerFill, this.pointerEdge, new RoundedRect(MarkCell(column, top, inCharacters).Deflate(0.5), 4));
    }

    /// <summary>A solid block behind byte <paramref name="column"/> of row <paramref name="row"/>
    /// in one panel, with the byte drawn again on it in <see cref="ByteMarkForeground"/>.</summary>
    private void DrawSolidMark(DrawingContext context, Typeface typeface, long row, int column, double top, bool inCharacters, IBrush block)
    {
        Span<byte> bytes = stackalloc byte[HexRows.BytesPerRow];
        if (this.document is null || this.document.ReadRow(row, bytes) <= column)
            return;

        byte value = bytes[column];
        var cell = MarkCell(column, top, inCharacters);
        context.DrawRectangle(block, null, new RoundedRect(cell, 4));

        var ink = ByteMarkForeground ?? Background ?? Brushes.White;
        string text = inCharacters
            ? HexRows.ClassOf(value) == HexByteClass.Printable ? ((char)value).ToString() : "."
            : value.ToString("X2");
        using var layout = new TextLayout(text, typeface, FontSize, ink);
        double x = inCharacters ? CharacterCellLeft(column) + CharacterSpacing / 2 : HexTextLeft + HexByteColumn(column) * this.charWidth;
        layout.Draw(context, new Point(x, CentreInRow(layout, top)));
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

        if (this.hexHeader is null || this.characterHeader is null || this.offsetHeader is null)
            return;

        double y = (HeaderHeight - this.hexHeader.Height) / 2;
        this.offsetHeader.Draw(context, new Point(PanelPadding, y));
        using (context.PushClip(new Rect(this.gutterWidth, 0, Math.Max(0, right - this.gutterWidth), HeaderHeight)))
        {
            this.hexHeader.Draw(context, new Point(HexTextLeft, y));
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
                double gap = HexTextLeft + (HexByteColumn(HexRows.BytesPerRow / 2) - 1) * this.charWidth + this.charWidth / 2;
                context.DrawLine(group, new Point(gap, RowsTop), new Point(gap, height - 4));
            }
        }
    }

    private RowLayouts LayoutFor(long row, Typeface typeface)
    {
        if (this.layouts.TryGetValue(row, out var cached))
            return cached;

        int count = this.document!.ReadRow(row, this.rowBytes);
        HexRows.Format(this.rowBytes.AsSpan(0, count), HexRows.StartOf(row), this.offsetDigits, this.line);

        var foreground = Foreground ?? Brushes.Black;
        int hexColumn = HexRows.HexColumn(this.offsetDigits);
        int characterColumn = HexRows.CharacterColumn(this.offsetDigits);
        var laid = new RowLayouts(
            new TextLayout(new string(this.line, 0, this.offsetDigits), typeface, FontSize, GutterForeground ?? foreground),
            new TextLayout(new string(this.line, hexColumn, HexTextChars), typeface, FontSize, foreground,
                TextAlignment.Left, TextWrapping.NoWrap, textStyleOverrides: StylesFor(count, typeface, hex: true)),
            new TextLayout(new string(this.line, characterColumn, count), typeface, FontSize, foreground,
                TextAlignment.Left, TextWrapping.NoWrap, letterSpacing: CharacterSpacing,
                textStyleOverrides: StylesFor(count, typeface, hex: false)));
        this.layouts[row] = laid;
        return laid;
    }

    /// <summary>The coloured spans of one panel's text for a row of <paramref name="count"/> bytes:
    /// each byte that is not printable ASCII in its class's colour, in order of position, which a
    /// layout requires. Printable bytes take the layout's own foreground.</summary>
    private List<ValueSpan<TextRunProperties>> StylesFor(int count, Typeface typeface, bool hex)
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
                styles.Add(hex ? new(HexByteColumn(i), 2, run) : new(i, 1, run));
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
        this.hexPanelWidth = PanelPadding + (HexTextChars) * this.charWidth + PanelPadding;
        this.characterPanelWidth = CharacterPanelPadding + HexRows.BytesPerRow * (this.charWidth + CharacterSpacing) + CharacterPanelPadding;

        var header = new char[HexTextChars];
        Array.Fill(header, ' ');
        for (int i = 0; i < HexRows.BytesPerRow; i++)
        {
            header[HexByteColumn(i)] = '0';
            header[HexByteColumn(i) + 1] = "0123456789ABCDEF"[i];
        }

        // Semibold, which a monospaced font draws at the same advance, so the labels stay over
        // their columns.
        var labels = new Typeface(FontFamily, FontStyle.Normal, FontWeight.SemiBold);
        var muted = GutterForeground ?? Foreground ?? Brushes.Gray;
        this.hexHeader?.Dispose();
        this.characterHeader?.Dispose();
        this.offsetHeader?.Dispose();
        this.hexHeader = new TextLayout(new string(header), labels, FontSize, muted);
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
        foreach (var laid in this.layouts.Values)
            laid.Dispose();
        this.layouts.Clear();
        this.zeroRun = null;
        this.otherRun = null;
    }
}
