using System;
using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// The byte range a double-click selects.
///
/// Word boundaries are a <i>character</i> question, so unlike <see cref="RawCaretStops"/> this
/// works over a row's decoded text and maps the answer back to bytes - which is cheap, because a
/// row is capped at the wrap width and is already decoded and cached by the time a click lands on
/// it. The only unbounded case is a document with no boundaries at all (one multi-GB run of
/// letters), so the walk stops at <see cref="MaxWordBytes"/> and reports that it refused rather
/// than turning the whole file into a selection the user did not ask for.
/// </summary>
public static class RawWordStops
{
    /// <summary>
    /// How far a word may run before the walk gives up. A run longer than this is not a word,
    /// it is unsegmented data, and finding its far end means decoding rows until it appears -
    /// work a click must not do.
    /// </summary>
    public const int MaxWordBytes = 64 * 1024;

    /// <summary>What a character counts as when the selection grows outwards from the click.</summary>
    private enum WordClass
    {
        /// <summary>Letters, digits, underscore, and anything outside the BMP.</summary>
        Word,

        Whitespace,

        Punctuation,

        /// <summary>
        /// A display substitution - U+FFFD for a run of invalid bytes, or one of the glyphs
        /// <see cref="RawRowReader.IsSubstitutionGlyph"/> names: a Control Picture for a C0
        /// control or DEL, and the marks standing for NEL, LINE SEPARATOR and PARAGRAPH
        /// SEPARATOR. It stands for bytes that are not text, so it never joins a run: selecting
        /// exactly one of them is what makes a corrupt sequence a single thing to delete.
        /// </summary>
        Opaque
    }

    /// <summary>
    /// The word containing <paramref name="offset"/>, as an absolute byte range, or null when the
    /// run around the offset is longer than <see cref="MaxWordBytes"/>. An offset with nothing to
    /// select around it - an empty row, or a document with no rows yet - comes back as an empty
    /// range at the offset itself, which is a caret placement rather than a refusal.
    /// </summary>
    public static (long Start, long End)? WordAt(IRawRowIndex rows, IByteSource source, long offset)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(source);

        long clamped = Math.Clamp(offset, 0, source.AvailableLength);
        var empty = (clamped, clamped);

        if (rows.RowCount == 0)
            return empty;

        int rowIndex = rows.RowForOffset(clamped) ?? rows.RowCount - 1;
        var cursor = Load(rows, source, rowIndex);

        int charIndex = cursor.Decoded.CharIndexForByte((int)(clamped - cursor.Info.Start));

        // Past the last character of the row: the click was at the end of a line or of the
        // document, where the word to select is the one ending there.
        if (charIndex >= cursor.Decoded.Text.Length)
        {
            if (cursor.Decoded.Text.Length == 0)
                return empty;

            charIndex = cursor.Decoded.Text.Length - 1;
        }

        // Both halves of a surrogate pair report the same byte, so a click inside one resolves to
        // the low half. Step back to the high half, where the pair is classified as one thing.
        if (char.IsLowSurrogate(cursor.Decoded.Text[charIndex]) && charIndex > 0
            && char.IsHighSurrogate(cursor.Decoded.Text[charIndex - 1]))
        {
            charIndex--;
        }

        var (classification, width) = ClassifyAt(cursor.Decoded.Text, charIndex);
        long start = cursor.Info.Start + cursor.Decoded.ByteOffsetForChar(charIndex);
        long end = cursor.Info.Start + cursor.Decoded.ByteOffsetForChar(charIndex + width);

        if (classification == WordClass.Opaque)
            return (start, end);

        if (ExtendBackwards(rows, source, cursor, charIndex, classification, end) is not long wordStart)
            return null;

        // Measured from the start the backwards walk found, so the cap bounds the whole run
        // rather than each half of it.
        if (ExtendForwards(rows, source, cursor, charIndex + width, classification, wordStart) is not long wordEnd)
            return null;

        return (wordStart, wordEnd);
    }

    /// <summary>
    /// The first byte of the run, walking left from <paramref name="charIndex"/> and across any
    /// soft wrap - a word split by the wrap width is one word - but never across a real line end.
    /// Null when the run passes <see cref="MaxWordBytes"/>.
    /// </summary>
    private static long? ExtendBackwards(
        IRawRowIndex rows,
        IByteSource source,
        RowCursor cursor,
        int charIndex,
        WordClass classification,
        long runEnd)
    {
        while (true)
        {
            while (charIndex > 0)
            {
                var (previous, width) = ClassifyBefore(cursor.Decoded.Text, charIndex);
                if (previous != classification)
                    return cursor.Info.Start + cursor.Decoded.ByteOffsetForChar(charIndex);

                charIndex -= width;
                if (runEnd - (cursor.Info.Start + cursor.Decoded.ByteOffsetForChar(charIndex)) > MaxWordBytes)
                    return null;
            }

            // At the row's first character. Only a row the wrap width broke continues leftwards;
            // after a real '\n' the run has ended.
            if (cursor.RowIndex == 0)
                return cursor.Info.Start;

            var previousRow = Load(rows, source, cursor.RowIndex - 1);
            if (!previousRow.Info.IsSoftWrapped || previousRow.Decoded.Text.Length == 0)
                return cursor.Info.Start;

            cursor = previousRow;
            charIndex = cursor.Decoded.Text.Length;
        }
    }

    /// <summary>
    /// The byte just past the end of the run, walking right from <paramref name="charIndex"/> on
    /// the same terms as <see cref="ExtendBackwards"/>.
    /// </summary>
    private static long? ExtendForwards(
        IRawRowIndex rows,
        IByteSource source,
        RowCursor cursor,
        int charIndex,
        WordClass classification,
        long runStart)
    {
        while (true)
        {
            var text = cursor.Decoded.Text;
            while (charIndex < text.Length)
            {
                var (next, width) = ClassifyAt(text, charIndex);
                if (next != classification)
                    return cursor.Info.Start + cursor.Decoded.ByteOffsetForChar(charIndex);

                charIndex += width;
                if (cursor.Info.Start + cursor.Decoded.ByteOffsetForChar(charIndex) - runStart > MaxWordBytes)
                    return null;
            }

            long rowEnd = cursor.Info.Start + cursor.Decoded.DisplayByteLength;
            if (!cursor.Info.IsSoftWrapped || cursor.RowIndex + 1 >= rows.RowCount)
                return rowEnd;

            cursor = Load(rows, source, cursor.RowIndex + 1);
            charIndex = 0;
        }
    }

    /// <summary>
    /// The class of the character at <paramref name="charIndex"/> and how many chars it occupies -
    /// two for a surrogate pair, which must be stepped over as one thing because both halves
    /// report the same byte offset.
    /// </summary>
    private static (WordClass Class, int Width) ClassifyAt(string text, int charIndex)
    {
        char c = text[charIndex];
        if (char.IsHighSurrogate(c) && charIndex + 1 < text.Length && char.IsLowSurrogate(text[charIndex + 1]))
            return (WordClass.Word, 2); // astral: an emoji or a rare letter, one thing either way

        return (Classify(c), 1);
    }

    /// <summary>The class of the character ending at <paramref name="charIndex"/>, walking left.</summary>
    private static (WordClass Class, int Width) ClassifyBefore(string text, int charIndex)
    {
        char c = text[charIndex - 1];
        if (char.IsLowSurrogate(c) && charIndex - 2 >= 0 && char.IsHighSurrogate(text[charIndex - 2]))
            return (WordClass.Word, 2);

        return (Classify(c), 1);
    }

    private static WordClass Classify(char c)
    {
        if (c == Rune.ReplacementChar.Value || RawRowReader.IsSubstitutionGlyph(c))
            return WordClass.Opaque;
        if (char.IsWhiteSpace(c))
            return WordClass.Whitespace;
        if (char.IsLetterOrDigit(c) || c == '_')
            return WordClass.Word;

        return WordClass.Punctuation;
    }

    private static RowCursor Load(IRawRowIndex rows, IByteSource source, int rowIndex)
    {
        var info = rows.GetRowInfo(rowIndex);
        return new RowCursor(rowIndex, info, RawRowDecoder.Decode(source, info.Start, info.End, info.IsSoftWrapped));
    }

    private readonly record struct RowCursor(int RowIndex, RawRowInfo Info, RawDecodedRow Decoded);
}
