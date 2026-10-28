using System;
using System.Buffers;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Xml.Indexing;

/// <summary>What a resumable <see cref="XmlStructuralScanner"/> primitive found.</summary>
public enum XmlScanOutcome
{
    /// <summary>The construct ended; its end offset was returned.</summary>
    Found,

    /// <summary>Everything that has arrived was read and the construct did not end in it, on a
    /// source that is still receiving. Ask again with the same <see cref="XmlScanResume"/> once
    /// more has arrived: it carries on from where this stopped.</summary>
    Incomplete,

    /// <summary>The construct runs past the end of a settled source: it is never closed.</summary>
    Unterminated,
}

/// <summary>What <see cref="XmlStructuralScanner.TrySkipElement"/> found.</summary>
public enum XmlSkipOutcome
{
    /// <summary>The element ended; its end offset was returned.</summary>
    Skipped,

    /// <summary>The element runs past the bytes available so far on a source that is still
    /// receiving. Ask again once more has arrived.</summary>
    Incomplete,

    /// <summary>The scanner cannot follow it: it does not start at a start tag, holds a DOCTYPE or
    /// markup it does not know, or runs past the end of a settled source.</summary>
    NeedsFullParse,
}

/// <summary>
/// Where a scan of one construct (a tag, comment, CDATA section, processing instruction or
/// DOCTYPE) has got to, so that a source still receiving can be asked again without reading the
/// construct from its start: a 500 MB comment arriving in pieces is read once, not once per piece.
/// Made by the starting method for the kind of construct, and handed to the matching
/// <see cref="XmlStructuralScanner"/> primitive until it answers
/// <see cref="XmlScanOutcome.Found"/>.
/// </summary>
public struct XmlScanResume
{
    internal long Cursor;

    // Which quote a tag or DOCTYPE is inside, if any.
    internal byte Quote;

    // How a DOCTYPE is nested; see XmlStructuralScanner.DoctypeEnd.
    internal byte Mode;

    // How many bytes at the end of what was read already match the start of a comment, CDATA or
    // processing-instruction terminator, so a terminator split across spans is still found.
    internal int Carry;

    /// <summary>Where the scan has read up to: everything before it is accounted for.</summary>
    public readonly long Position => Cursor;

    /// <summary>For a start tag whose <c>&lt;</c> is at <paramref name="start"/>.</summary>
    public static XmlScanResume Tag(long start) => new() { Cursor = start + 1 };

    /// <summary>For an end tag whose <c>&lt;</c> is at <paramref name="start"/>.</summary>
    public static XmlScanResume CloseTag(long start) => new() { Cursor = start + 2 };

    /// <summary>For a comment whose <c>&lt;</c> is at <paramref name="start"/>.</summary>
    public static XmlScanResume Comment(long start) => new() { Cursor = start + 4 };

    /// <summary>For a CDATA section whose <c>&lt;</c> is at <paramref name="start"/>.</summary>
    public static XmlScanResume CData(long start) => new() { Cursor = start + 9 };

    /// <summary>For a processing instruction (or the XML declaration) whose <c>&lt;</c> is at
    /// <paramref name="start"/>.</summary>
    public static XmlScanResume ProcessingInstruction(long start) => new() { Cursor = start + 2 };

    /// <summary>For a DOCTYPE whose <c>&lt;</c> is at <paramref name="start"/>.</summary>
    public static XmlScanResume Doctype(long start) => new() { Cursor = start + 9 };
}

/// <summary>
/// The span-level primitives the XML index and the tree reader both use to move over markup: where
/// a tag, comment, CDATA section, processing instruction or DOCTYPE ends, where the next markup
/// is, and where an element ends. None allocates, and none requires a construct to be contiguous:
/// each advances by the length the source returned, treats an empty span as the end of what is
/// there, and keeps what it needs between spans in an <see cref="XmlScanResume"/> (the quote it is
/// inside, how much of a terminator it has matched), so a source that serves a byte at a time
/// gives the same answers as a mapping.
///
/// They do not validate. A construct ends where its terminator is, which is all a structural
/// scan needs; whether the names inside it make sense is not asked.
/// </summary>
public static class XmlStructuralScanner
{
    /// <summary>Bytes asked of the source per read. Only affects how often a split or growing
    /// source is re-asked; a single-buffer source serves each request whole.</summary>
    private const int ReadStride = 64 * 1024;

    /// <summary>XML whitespace: space, tab, carriage return and line feed.</summary>
    internal static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \t\r\n"u8);

    /// <summary>The bytes that end an element name or a processing instruction's target.</summary>
    internal static readonly SearchValues<byte> NameEnd = SearchValues.Create(" \t\r\n/>?"u8);

    private static readonly SearchValues<byte> DoctypeOutsideSubset = SearchValues.Create("\"'[>"u8);

    private static readonly SearchValues<byte> DoctypeInsideSubset = SearchValues.Create("\"'<]"u8);

    // DOCTYPE modes: how far into a markup declaration of the internal subset the scan is.
    private const byte Plain = 0;
    private const byte Subset = 1;
    private const byte SubsetAfterLt = 2;
    private const byte SubsetAfterLtBang = 3;
    private const byte SubsetAfterLtBangDash = 4;
    private const byte SubsetComment = 5;
    private const byte SubsetInstruction = 6;

    /// <summary>
    /// The end of the start tag (or end tag) whose <c>&lt;</c> began the scan: one past its
    /// <c>&gt;</c>, quote-aware, so an attribute value may hold <c>&gt;</c> and the other quote.
    /// Whether it was self-closing is <see cref="IsSelfClosing"/>.
    /// </summary>
    public static XmlScanOutcome TagEnd(IByteSource source, ref XmlScanResume resume, out long end)
    {
        // Read before the spans, never after: a source that settles between the last span and
        // this read would otherwise be reported unterminated with bytes unread.
        bool settled = source.LengthSettled;
        while (true)
        {
            var span = source.GetContiguousSpan(resume.Cursor, ReadStride);
            if (span.IsEmpty)
            {
                end = resume.Cursor;
                return settled ? XmlScanOutcome.Unterminated : XmlScanOutcome.Incomplete;
            }

            if (TagEndIn(span, ref resume.Quote, out int at))
            {
                end = resume.Cursor + at;
                resume.Cursor = end;
                return XmlScanOutcome.Found;
            }

            resume.Cursor += span.Length;
        }
    }

    /// <summary>One span's share of <see cref="TagEnd(IByteSource, ref XmlScanResume, out long)"/>:
    /// the index one past the tag's <c>&gt;</c> in <paramref name="span"/>, or false with
    /// <paramref name="quote"/> carrying what the span ended inside.</summary>
    internal static bool TagEndIn(ReadOnlySpan<byte> span, ref byte quote, out int end)
    {
        int i = 0;
        while (true)
        {
            if (quote != 0)
            {
                int close = span.Slice(i).IndexOf(quote);
                if (close < 0)
                    break;

                i += close + 1;
                quote = 0;
            }

            int special = span.Slice(i).IndexOfAny((byte)'>', (byte)'"', (byte)'\'');
            if (special < 0)
                break;

            i += special;
            byte found = span[i++];
            if (found == (byte)'>')
            {
                end = i;
                return true;
            }

            quote = found;
        }

        end = 0;
        return false;
    }

    /// <summary>
    /// <see cref="TagEnd(IByteSource, ref XmlScanResume, out long)"/> in one call, from the
    /// <c>&lt;</c> at <paramref name="start"/>.
    /// </summary>
    public static XmlScanOutcome TagEnd(IByteSource source, long start, out long end)
    {
        var resume = XmlScanResume.Tag(start);
        return TagEnd(source, ref resume, out end);
    }

    /// <summary>Whether the tag that ended at <paramref name="tagEnd"/> was self-closing: the byte
    /// before its <c>&gt;</c> is <c>/</c>. Right for a quote-aware end, since a <c>/</c> inside an
    /// attribute value is followed by its closing quote.</summary>
    public static bool IsSelfClosing(IByteSource source, long tagEnd) => source.ByteAt(tagEnd - 2) == (byte)'/';

    /// <summary>
    /// The end of the end tag whose <c>&lt;</c> began the scan: one past the next <c>&gt;</c>. Not
    /// quote-aware, since an end tag holds no attributes and a stray quote in a damaged one
    /// should not swallow the rest of the document.
    /// </summary>
    public static XmlScanOutcome CloseTagEnd(IByteSource source, ref XmlScanResume resume, out long end)
    {
        bool settled = source.LengthSettled;
        while (true)
        {
            var span = source.GetContiguousSpan(resume.Cursor, ReadStride);
            if (span.IsEmpty)
            {
                end = resume.Cursor;
                return settled ? XmlScanOutcome.Unterminated : XmlScanOutcome.Incomplete;
            }

            int at = span.IndexOf((byte)'>');
            if (at >= 0)
            {
                end = resume.Cursor + at + 1;
                resume.Cursor = end;
                return XmlScanOutcome.Found;
            }

            resume.Cursor += span.Length;
        }
    }

    /// <summary>The end of the comment whose <c>&lt;</c> began the scan: one past its
    /// <c>--&gt;</c>.</summary>
    public static XmlScanOutcome CommentEnd(IByteSource source, ref XmlScanResume resume, out long end)
        => MarkerEnd(source, ref resume, (byte)'-', 2, out end);

    /// <summary>The end of the CDATA section whose <c>&lt;</c> began the scan: one past its
    /// <c>]]&gt;</c>.</summary>
    public static XmlScanOutcome CDataEnd(IByteSource source, ref XmlScanResume resume, out long end)
        => MarkerEnd(source, ref resume, (byte)']', 2, out end);

    /// <summary>The end of the processing instruction whose <c>&lt;</c> began the scan: one past
    /// its <c>?&gt;</c>.</summary>
    public static XmlScanOutcome ProcessingInstructionEnd(IByteSource source, ref XmlScanResume resume, out long end)
        => MarkerEnd(source, ref resume, (byte)'?', 1, out end);

    /// <summary>The three terminators are each a marker byte repeated and then <c>&gt;</c>
    /// (<c>--&gt;</c>, <c>]]&gt;</c>, <c>?&gt;</c>), which is what lets a split one be carried as a
    /// single count.</summary>
    private static XmlScanOutcome MarkerEnd(IByteSource source, ref XmlScanResume resume, byte marker, int markers, out long end)
    {
        bool settled = source.LengthSettled;
        while (true)
        {
            var span = source.GetContiguousSpan(resume.Cursor, ReadStride);
            if (span.IsEmpty)
            {
                end = resume.Cursor;
                return settled ? XmlScanOutcome.Unterminated : XmlScanOutcome.Incomplete;
            }

            if (MarkerEndIn(span, marker, markers, ref resume.Carry, out int at))
            {
                end = resume.Cursor + at;
                resume.Cursor = end;
                return XmlScanOutcome.Found;
            }

            resume.Cursor += span.Length;
        }
    }

    /// <summary>
    /// The index one past a <c>&gt;</c> preceded by at least <paramref name="markers"/> marker
    /// bytes, or false. <paramref name="carry"/> is how many marker bytes ended the previous span,
    /// and is updated to how many end this one. Searches for the <c>&gt;</c> - the rare byte -
    /// rather than the marker, which is common in comment text.
    /// </summary>
    internal static bool MarkerEndIn(ReadOnlySpan<byte> span, byte marker, int markers, ref int carry, out int end)
    {
        int from = 0;
        while (true)
        {
            int found = span.Slice(from).IndexOf((byte)'>');
            if (found < 0)
                break;

            int close = from + found;
            int run = 0;
            while (run < markers && close - 1 - run >= 0 && span[close - 1 - run] == marker)
                run++;

            // Only a '>' with nothing but markers before it in this span can be joined to the
            // markers that ended the last one.
            if (run < markers && run == close)
                run = Math.Min(markers, run + carry);

            if (run >= markers)
            {
                end = close + 1;
                carry = 0;
                return true;
            }

            from = close + 1;
        }

        int tail = 0;
        while (tail < markers && tail < span.Length && span[span.Length - 1 - tail] == marker)
            tail++;

        carry = tail == span.Length ? Math.Min(markers, tail + carry) : tail;
        end = 0;
        return false;
    }

    /// <summary>
    /// The end of the DOCTYPE whose <c>&lt;</c> began the scan: one past the <c>&gt;</c> that is
    /// outside quotes and outside the internal subset (<c>[...]</c>). Inside the subset, comments
    /// and processing instructions and quoted literals may hold <c>]</c> and <c>&gt;</c>, so those
    /// are stepped over as units.
    /// </summary>
    public static XmlScanOutcome DoctypeEnd(IByteSource source, ref XmlScanResume resume, out long end)
    {
        bool settled = source.LengthSettled;
        while (true)
        {
            var span = source.GetContiguousSpan(resume.Cursor, ReadStride);
            if (span.IsEmpty)
            {
                end = resume.Cursor;
                return settled ? XmlScanOutcome.Unterminated : XmlScanOutcome.Incomplete;
            }

            int i = 0;
            while (i < span.Length)
            {
                if (resume.Quote != 0)
                {
                    int close = span.Slice(i).IndexOf(resume.Quote);
                    if (close < 0)
                    {
                        i = span.Length;
                        break;
                    }

                    i += close + 1;
                    resume.Quote = 0;
                    continue;
                }

                switch (resume.Mode)
                {
                    case Plain:
                    {
                        int special = span.Slice(i).IndexOfAny(DoctypeOutsideSubset);
                        if (special < 0)
                        {
                            i = span.Length;
                            break;
                        }

                        i += special;
                        byte b = span[i++];
                        if (b == (byte)'>')
                        {
                            end = resume.Cursor + i;
                            resume.Cursor = end;
                            return XmlScanOutcome.Found;
                        }

                        if (b == (byte)'[')
                            resume.Mode = Subset;
                        else
                            resume.Quote = b;
                        break;
                    }

                    case Subset:
                    {
                        int special = span.Slice(i).IndexOfAny(DoctypeInsideSubset);
                        if (special < 0)
                        {
                            i = span.Length;
                            break;
                        }

                        i += special;
                        byte b = span[i++];
                        if (b == (byte)'<')
                            resume.Mode = SubsetAfterLt;
                        else if (b == (byte)']')
                            resume.Mode = Plain;
                        else
                            resume.Quote = b;
                        break;
                    }

                    case SubsetAfterLt:
                        // "<?" opens an instruction and "<!" may open a comment; anything else
                        // is read again as ordinary subset content.
                        if (span[i] == (byte)'?')
                        {
                            resume.Mode = SubsetInstruction;
                            resume.Carry = 0;
                            i++;
                        }
                        else if (span[i] == (byte)'!')
                        {
                            resume.Mode = SubsetAfterLtBang;
                            i++;
                        }
                        else
                        {
                            resume.Mode = Subset;
                        }

                        break;

                    case SubsetAfterLtBang:
                        if (span[i] == (byte)'-')
                        {
                            resume.Mode = SubsetAfterLtBangDash;
                            i++;
                        }
                        else
                        {
                            resume.Mode = Subset;
                        }

                        break;

                    case SubsetAfterLtBangDash:
                        if (span[i] == (byte)'-')
                        {
                            resume.Mode = SubsetComment;
                            resume.Carry = 0;
                            i++;
                        }
                        else
                        {
                            resume.Mode = Subset;
                        }

                        break;

                    case SubsetComment:
                        if (MarkerEndIn(span.Slice(i), (byte)'-', 2, ref resume.Carry, out int commentEnd))
                        {
                            i += commentEnd;
                            resume.Mode = Subset;
                        }
                        else
                        {
                            i = span.Length;
                        }

                        break;

                    default: // SubsetInstruction
                        if (MarkerEndIn(span.Slice(i), (byte)'?', 1, ref resume.Carry, out int instructionEnd))
                        {
                            i += instructionEnd;
                            resume.Mode = Subset;
                        }
                        else
                        {
                            i = span.Length;
                        }

                        break;
                }
            }

            resume.Cursor += span.Length;
        }
    }

    /// <summary>
    /// The next <c>&lt;</c> at or after <paramref name="from"/>: the vectorised skip through
    /// text. <see cref="XmlScanOutcome.Found"/> with its offset; otherwise the text runs to the end
    /// of what is there (<see cref="XmlScanOutcome.Unterminated"/> when that is the end of the
    /// document, <see cref="XmlScanOutcome.Incomplete"/> when more may follow).
    /// </summary>
    public static XmlScanOutcome NextMarkup(IByteSource source, long from, out long markup)
    {
        bool settled = source.LengthSettled;
        long offset = from;
        while (true)
        {
            var span = source.GetContiguousSpan(offset, ReadStride);
            if (span.IsEmpty)
            {
                markup = offset;
                return settled ? XmlScanOutcome.Unterminated : XmlScanOutcome.Incomplete;
            }

            int at = span.IndexOf((byte)'<');
            if (at >= 0)
            {
                markup = offset + at;
                return XmlScanOutcome.Found;
            }

            offset += span.Length;
        }
    }

    /// <summary>
    /// Finds the end of the element whose start tag begins at <paramref name="start"/>: one past
    /// its matching end tag, by counting start and end tags and stepping over comments, CDATA
    /// sections and processing instructions as units, so a <c>&lt;</c> inside one does not count.
    /// A self-closing start tag is an element of its own length.
    ///
    /// It does not match names: an end tag closes whatever is open. Like
    /// <c>JsonStructuralScanner.TrySkipValue</c> it answers
    /// <see cref="XmlSkipOutcome.NeedsFullParse"/> for what it cannot decide, and the caller reads
    /// the element tag by tag instead.
    /// </summary>
    public static XmlSkipOutcome TrySkipElement(IByteSource source, long start, out long end)
    {
        end = start;
        Span<byte> head = stackalloc byte[9];
        long offset = start;
        int depth = 0;

        while (true)
        {
            var found = NextMarkup(source, offset, out long markup);
            if (found != XmlScanOutcome.Found)
                return found == XmlScanOutcome.Incomplete ? XmlSkipOutcome.Incomplete : XmlSkipOutcome.NeedsFullParse;

            // Read the settled flag first: a short read is the end only if it was settled before.
            bool settled = source.LengthSettled;
            int copied = source.CopyTo(markup, head);
            if (copied < 2 || (head[1] == (byte)'!' && copied < 9))
            {
                if (!settled)
                    return XmlSkipOutcome.Incomplete;
                if (copied < 2)
                    return XmlSkipOutcome.NeedsFullParse;
            }

            var tag = head.Slice(0, copied);
            XmlScanOutcome outcome;
            long constructEnd;
            if (tag[1] == (byte)'/')
            {
                if (depth == 0)
                    return XmlSkipOutcome.NeedsFullParse; // the element began with an end tag
                var close = XmlScanResume.CloseTag(markup);
                outcome = CloseTagEnd(source, ref close, out constructEnd);
                if (outcome == XmlScanOutcome.Found && --depth == 0)
                {
                    end = constructEnd;
                    return XmlSkipOutcome.Skipped;
                }
            }
            else if (tag[1] == (byte)'?')
            {
                if (depth == 0)
                    return XmlSkipOutcome.NeedsFullParse;
                var instruction = XmlScanResume.ProcessingInstruction(markup);
                outcome = ProcessingInstructionEnd(source, ref instruction, out constructEnd);
            }
            else if (tag[1] == (byte)'!')
            {
                if (depth == 0)
                    return XmlSkipOutcome.NeedsFullParse;
                if (tag.StartsWith("<!--"u8))
                {
                    var comment = XmlScanResume.Comment(markup);
                    outcome = CommentEnd(source, ref comment, out constructEnd);
                }
                else if (tag.StartsWith("<![CDATA["u8))
                {
                    var cdata = XmlScanResume.CData(markup);
                    outcome = CDataEnd(source, ref cdata, out constructEnd);
                }
                else
                {
                    return XmlSkipOutcome.NeedsFullParse; // a DOCTYPE or markup it does not know
                }
            }
            else
            {
                var open = XmlScanResume.Tag(markup);
                outcome = TagEnd(source, ref open, out constructEnd);
                if (outcome == XmlScanOutcome.Found)
                {
                    if (!IsSelfClosing(source, constructEnd))
                    {
                        depth++;
                    }
                    else if (depth == 0)
                    {
                        end = constructEnd;
                        return XmlSkipOutcome.Skipped;
                    }
                }
            }

            if (outcome != XmlScanOutcome.Found)
                return outcome == XmlScanOutcome.Incomplete ? XmlSkipOutcome.Incomplete : XmlSkipOutcome.NeedsFullParse;

            offset = constructEnd;
        }
    }
}
