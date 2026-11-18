using System;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Features.Hex;

/// <summary>
/// The hex view's caret and selection, as two boundaries between bytes: <see cref="Anchor"/> where
/// the selection started and <see cref="Offset"/> where it was taken to. The bytes between them
/// are selected; when they are equal nothing is, and the caret sits on the byte at
/// <see cref="Offset"/>.
/// </summary>
public readonly record struct HexCaret(long Anchor, long Offset)
{
    public static HexCaret At(long offset) => new(offset, offset);

    /// <summary>From byte <paramref name="from"/> to byte <paramref name="to"/>, both selected,
    /// whichever way round - what a drag between two bytes selects.</summary>
    public static HexCaret Spanning(long from, long to) => to >= from ? new(from, to + 1) : new(from + 1, to);

    /// <summary>From the group holding byte <paramref name="from"/> to the group holding byte
    /// <paramref name="to"/>, both whole - what a drag across grouped bytes selects. A selection
    /// that runs past the end is kept inside it when it is set.</summary>
    public static HexCaret SpanningGroups(long from, long to, int groupSize)
    {
        long fromStart = HexRows.GroupStart(from, groupSize);
        long toStart = HexRows.GroupStart(to, groupSize);
        return toStart >= fromStart
            ? new(fromStart, toStart + groupSize)
            : new(fromStart + groupSize, toStart);
    }

    public long Start => Math.Min(Anchor, Offset);

    public long End => Math.Max(Anchor, Offset);

    public long Length => End - Start;

    public bool IsEmpty => Anchor == Offset;

    /// <summary>The byte the caret is on: the moving end's own byte - the last one selected when
    /// the selection was taken forward, the first when backward, and the byte at
    /// <see cref="Offset"/> when nothing is selected.</summary>
    public long Byte => Offset > Anchor ? Offset - 1 : Offset;
}

/// <summary>Where a key takes the caret.</summary>
public enum HexMove
{
    Left,
    Right,
    Up,
    Down,
    PageUp,
    PageDown,
    RowStart,
    RowEnd,
    DocumentStart,
    DocumentEnd,
}

/// <summary>
/// Owns the hex view's <see cref="HexCaret"/> and moves it - by click, drag and key - keeping it
/// inside the document: on a byte when nothing is selected, up to the end when a selection
/// reaches it. Every move is arithmetic on the 16-byte rows, so nothing is read and a caret
/// anywhere in a multi-gigabyte document moves as cheaply as at its start.
///
/// A selection of up to <see cref="MaxKeptWidth"/> bytes - what the Values tab reads - is a width
/// the caret carries: a click or a plain move takes the selection there at the same width, so a
/// value can be read at place after place. A drag, a Shift move or the Values tab sets a new
/// width; <see cref="Clear"/> drops it.
///
/// It also keeps the rows on screen, which the view reports as it scrolls, so
/// <see cref="Position"/> can say where the user is after scrolling the caret away; and a reveal
/// asked for before the view could scroll, which the view takes when it can.
/// </summary>
public sealed class HexCursor(Func<long> length)
{
    /// <summary>The widest selection a click or plain move keeps: as many bytes as the Values tab
    /// reads.</summary>
    public const int MaxKeptWidth = HexValuesViewModel.MaxBytes;

    private HexCaret? placed;
    private bool revealPending;

    /// <summary>Raised when the caret or the selection moves.</summary>
    public event EventHandler? Moved;

    /// <summary>Raised when a reveal asks the view to bring the caret on screen.</summary>
    public event EventHandler? RevealRequested;

    /// <summary>The caret: at the first byte until it is placed, and null while the document has
    /// no bytes. A streamed document's caret appears with its first byte.</summary>
    public HexCaret? Caret => this.placed ?? (length() > 0 ? HexCaret.At(0) : null);

    /// <summary>The rows the view shows, set as it scrolls; null until it has laid out.</summary>
    public (long First, long Last)? ViewportRows { get; set; }

    /// <summary>
    /// Where the user is: the selection, or the caret as a zero-length range - while the caret's
    /// row is on screen. Scrolling moves the view and not the caret, so once the caret is
    /// scrolled away, the first byte of the top row is where they are (as the raw view decides it).
    /// </summary>
    public ByteRange? Position
    {
        get
        {
            if (Caret is not { } caret)
                return null;

            var atCaret = caret.IsEmpty ? ByteRange.At(caret.Offset) : new ByteRange(caret.Start, caret.Length);
            if (ViewportRows is not { } viewport)
                return atCaret;

            long row = HexRows.RowOf(caret.Byte);
            return row >= viewport.First && row <= viewport.Last ? atCaret : ByteRange.At(HexRows.StartOf(viewport.First));
        }
    }

    /// <summary>How many bytes a click or plain move selects: the selection's length while it is
    /// one to <see cref="MaxKeptWidth"/> bytes; null when nothing, or more, is selected.</summary>
    public int? KeptWidth => Caret is { IsEmpty: false, Length: <= MaxKeptWidth } caret ? (int)caret.Length : null;

    /// <summary>Puts the caret on byte <paramref name="offset"/>, selecting nothing - or, while a
    /// width is kept, selects that many bytes from it.</summary>
    public void Place(long offset)
        => Select(KeptWidth is { } width ? Kept(offset, width) : HexCaret.At(offset));

    /// <summary><paramref name="width"/> bytes from <paramref name="start"/>, cut at the end,
    /// taken forward as a drag across them would be.</summary>
    private HexCaret Kept(long start, int width) => new(start, Math.Min(start + width, length()));

    /// <summary>Drops the selection and the width it kept, leaving the caret where it started.</summary>
    public void Clear()
    {
        if (Caret is { IsEmpty: false } caret)
            Select(HexCaret.At(caret.Start));
    }

    /// <summary>Takes the selection from where it is anchored to byte <paramref name="offset"/>,
    /// that byte included - a shift-click. With <paramref name="groupSize"/> it takes the byte's
    /// whole group.</summary>
    public void ExtendTo(long offset, int groupSize = 1)
    {
        if (Caret is not { } caret)
            return;

        long start = HexRows.GroupStart(offset, groupSize);
        Select(offset >= caret.Anchor ? new HexCaret(caret.Anchor, start + groupSize) : new HexCaret(caret.Anchor, start));
    }

    public void SelectAll()
    {
        long end = length();
        if (end > 0)
            Select(new HexCaret(0, end));
    }

    /// <summary>Sets the caret and selection, kept inside the document.</summary>
    public void Select(HexCaret caret)
    {
        long end = length();
        if (end <= 0)
            return;

        long anchor = Math.Clamp(caret.Anchor, 0, end);
        long offset = Math.Clamp(caret.Offset, 0, end);

        // With nothing selected the caret is on a byte, so never past the last one.
        var next = anchor == offset ? HexCaret.At(Math.Min(offset, end - 1)) : new HexCaret(anchor, offset);
        if (next == this.placed)
            return;

        this.placed = next;
        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the caret by <paramref name="move"/>, a page being <paramref name="pageRows"/> rows.
    /// With <paramref name="extend"/> the anchor stays and the selection grows or shrinks to the
    /// new place; without it the selection goes and the caret lands on a byte. With a
    /// <paramref name="groupSize"/> it steps a group at a time and lands on a group's first byte,
    /// as the hex panel moves when its bytes are grouped.
    ///
    /// While a width is kept, a plain move takes the whole selection, a group at a time - or, with
    /// <paramref name="byWidth"/>, its own width at a time, to step through a run of values.
    /// </summary>
    public void Move(HexMove move, bool extend, int pageRows, int groupSize = 1, bool byWidth = false)
    {
        if (Caret is not { } caret)
            return;

        if (!extend && KeptWidth is { } width)
        {
            MoveKeepingWidth(caret.Start, move, width, pageRows, byWidth ? width : groupSize);
            return;
        }

        long end = length();
        long lastPlace = extend ? end : HexRows.GroupStart(end - 1, groupSize);
        long from = extend ? caret.Offset : HexRows.GroupStart(caret.Byte, groupSize);
        long rowStart = HexRows.StartOf(HexRows.RowOf(from));
        long page = (long)Math.Max(1, pageRows) * HexRows.BytesPerRow;
        long to = move switch
        {
            // Extending steps from a boundary to the next one a group away.
            HexMove.Left => extend ? HexRows.GroupStart(from - 1, groupSize) : from - groupSize,
            HexMove.Right => extend ? HexRows.GroupStart(from, groupSize) + groupSize : from + groupSize,
            HexMove.Up => from - HexRows.BytesPerRow,
            HexMove.Down => from + HexRows.BytesPerRow,
            HexMove.PageUp => from - page,
            HexMove.PageDown => from + page,
            HexMove.RowStart => rowStart,

            // Extending takes the selection to the end of the row, its last group included; a
            // caret alone lands on that group.
            HexMove.RowEnd => rowStart + (extend ? HexRows.BytesPerRow : HexRows.BytesPerRow - groupSize),
            HexMove.DocumentStart => 0,
            HexMove.DocumentEnd => lastPlace,
            _ => from,
        };

        // A step up or down that would leave the document stays on its row rather than jumping
        // to an end - except a page, which goes as far as it can, as an editor's does.
        if (to < 0)
            to = move is HexMove.Up ? from : 0;
        else if (to > lastPlace)
            to = move is HexMove.Down ? from : lastPlace;

        Select(extend ? new HexCaret(caret.Anchor, to) : HexCaret.At(to));
    }

    /// <summary>Moves a selection of <paramref name="width"/> bytes starting at
    /// <paramref name="from"/>, keeping its width, by <paramref name="step"/> bytes left or right.
    /// It never runs past the end, so near it the selection stops where it still fits.</summary>
    private void MoveKeepingWidth(long from, HexMove move, int width, int pageRows, int step)
    {
        long lastStart = Math.Max(0, length() - width);
        long rowStart = HexRows.StartOf(HexRows.RowOf(from));
        long page = (long)Math.Max(1, pageRows) * HexRows.BytesPerRow;
        long to = move switch
        {
            HexMove.Left => from - step,
            HexMove.Right => from + step,
            HexMove.Up => from - HexRows.BytesPerRow,
            HexMove.Down => from + HexRows.BytesPerRow,
            HexMove.PageUp => from - page,
            HexMove.PageDown => from + page,
            HexMove.RowStart => rowStart,
            HexMove.RowEnd => Math.Max(rowStart, rowStart + HexRows.BytesPerRow - width),
            HexMove.DocumentStart => 0,
            HexMove.DocumentEnd => lastStart,
            _ => from,
        };

        // As a caret does: a row step that would leave the document stays put, anything else
        // goes as far as it can.
        if (to < 0)
            to = move is HexMove.Up ? from : 0;
        else if (to > lastStart)
            to = move is HexMove.Down ? from : lastStart;

        Select(Kept(to, width));
    }

    /// <summary>Selects <paramref name="range"/> with the caret at its start - where a jump in
    /// from another view lands - and asks the view to bring it on screen.</summary>
    public void Reveal(ByteRange range)
    {
        Select(range.Length > 0 ? new HexCaret(range.End, range.Offset) : HexCaret.At(range.Offset));
        this.revealPending = true;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a reveal is waiting for the view, clearing it - the view takes it once it
    /// has a size to scroll in.</summary>
    public bool TakeReveal()
    {
        bool pending = this.revealPending;
        this.revealPending = false;
        return pending;
    }
}
