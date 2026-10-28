using System;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// XML for <see cref="TreeCursor"/>: finds the next child in an element or at the document level and
/// gets past an element without reading it. Kinds are <see cref="XmlNodeKind"/>; a node's row
/// identity (<c>ValueStart</c>, which is also <c>RowStart</c>) is the <c>&lt;</c> of any markup, or
/// the first non-whitespace byte of a run of text.
///
/// It decides which elements are containers by the rule <see cref="XmlSparseIndex"/> applies while
/// scanning - an element is one exactly when its content holds an element, a comment or a
/// processing instruction, looking through text and CDATA - because the index records containers
/// and the cursor trusts it. It does not validate, and keeps no state about the elements above
/// it, so a read can resume at any checkpoint. No construct is required to be contiguous: each
/// is walked through <see cref="XmlStructuralScanner"/>, which asks the source for what it needs a
/// span at a time.
/// </summary>
public sealed class XmlTreeReader(IByteSource source) : ITreeFormatReader
{
    private const int ReadStride = 4096;

    private delegate XmlScanOutcome ConstructEnd(IByteSource source, ref XmlScanResume resume, out long end);

    public byte DocumentKind => (byte)XmlNodeKind.Document;

    public bool TryReadChild(byte containerKind, ref long position, out TreeNode child, out long closeStart)
    {
        child = default;
        long at = position;
        while (true)
        {
            at = SkipWhitespace(at);
            closeStart = at;
            int first = Peek(at);
            if (first < 0)
                return false;

            if (first != (byte)'<')
            {
                position = at;
                child = new TreeNode(at, at, TextEnd(at), IsContainer: false, (byte)XmlNodeKind.Text);
                return true;
            }

            int second = Peek(at + 1);
            if (second == (byte)'/')
            {
                if (containerKind != (byte)XmlNodeKind.Document)
                    return false;

                // A close tag with nothing open: the index skips it, and so does the tree.
                var stray = XmlScanResume.CloseTag(at);
                XmlStructuralScanner.CloseTagEnd(source, ref stray, out at);
                continue;
            }

            position = at;
            child = ReadMarkup(at, second);
            return true;
        }
    }

    public long FirstChildPosition(long containerStart)
        => XmlStructuralScanner.TagEnd(source, containerStart, out long end) == XmlScanOutcome.Found ? end : source.AvailableLength;

    public long SkipValue(long containerStart) => XmlStructuralScanner.TrySkipElement(source, containerStart, out long end) switch
    {
        XmlSkipOutcome.Skipped => end,

        // Still arriving: the element runs on past everything read so far.
        XmlSkipOutcome.Incomplete => long.MaxValue,

        // Not markup the scanner can follow. Indexing reports the error; the tree treats the
        // rest of the document as this element's.
        _ => source.AvailableLength,
    };

    /// <summary>The close tag is the last <c>&lt;</c> before the element's end. Walked back a byte
    /// at a time: a close tag is a name and a <c>&gt;</c>, and reading a byte through the source
    /// never needs the range around it to be contiguous.</summary>
    public long CloseStart(long containerStart, long containerEnd)
    {
        long at = containerEnd - 1;
        while (at > containerStart && source.ByteAt(at) != (byte)'<')
            at--;

        return at;
    }

    /// <summary>Where the text run that begins at <paramref name="start"/> ends: the next
    /// <c>&lt;</c>, or the end of the data.</summary>
    public long TextEnd(long start)
    {
        XmlStructuralScanner.NextMarkup(source, start, out long markup);
        return markup;
    }

    private TreeNode ReadMarkup(long at, int second)
    {
        if (second == (byte)'!')
        {
            Span<byte> head = stackalloc byte[9];
            var tag = head.Slice(0, source.CopyTo(at, head));
            if (tag.StartsWith("<!--"u8))
                return Construct(at, XmlNodeKind.Comment, XmlScanResume.Comment(at), XmlStructuralScanner.CommentEnd);
            if (tag.StartsWith("<![CDATA["u8))
                return Construct(at, XmlNodeKind.CData, XmlScanResume.CData(at), XmlStructuralScanner.CDataEnd);
            if (tag.StartsWith("<!DOCTYPE"u8))
                return Construct(at, XmlNodeKind.Doctype, XmlScanResume.Doctype(at), XmlStructuralScanner.DoctypeEnd);

            // Not markup the index knows either; it carries on as text.
            return new TreeNode(at, at, TextEnd(at + 2), IsContainer: false, (byte)XmlNodeKind.Text);
        }

        if (second == (byte)'?')
        {
            var kind = IsXmlDeclaration(at) ? XmlNodeKind.XmlDeclaration : XmlNodeKind.ProcessingInstruction;
            return Construct(at, kind, XmlScanResume.ProcessingInstruction(at), XmlStructuralScanner.ProcessingInstructionEnd);
        }

        if (!IsNameStart(second))
            return new TreeNode(at, at, TextEnd(at + 1), IsContainer: false, (byte)XmlNodeKind.Text);

        return ReadElement(at);
    }

    private TreeNode Construct(long at, XmlNodeKind kind, XmlScanResume resume, ConstructEnd endOf)
    {
        endOf(source, ref resume, out long end);
        return new TreeNode(at, at, end, IsContainer: false, (byte)kind);
    }

    private TreeNode ReadElement(long at)
    {
        // A start tag that has not arrived whole, or never ends, is a leaf to the end of the data.
        if (XmlStructuralScanner.TagEnd(source, at, out long tagEnd) != XmlScanOutcome.Found)
            return Leaf(at, tagEnd, hasText: false);

        if (XmlStructuralScanner.IsSelfClosing(source, tagEnd))
            return Leaf(at, tagEnd, hasText: false);

        // The next markup decides it: its own close tag makes it a leaf (with text, or empty); a
        // child element, comment or processing instruction makes it a container. Text and CDATA do
        // not decide, so the look-ahead runs through them - one vectorised pass per piece.
        Span<byte> head = stackalloc byte[9];
        bool hasText = false;
        long scan = tagEnd;
        while (true)
        {
            long markup = NextMarkup(scan, ref hasText);
            if (markup < 0)
                return Leaf(at, -markup - 1, hasText);

            var tag = head.Slice(0, source.CopyTo(markup, head));
            if (tag.Length < 2)
                return Leaf(at, markup + tag.Length, hasText);

            byte next = tag[1];
            if (next == (byte)'/')
            {
                var close = XmlScanResume.CloseTag(markup);
                XmlStructuralScanner.CloseTagEnd(source, ref close, out long closeEnd);
                return Leaf(at, closeEnd, hasText);
            }

            if (next == (byte)'?' || tag.StartsWith("<!--"u8) || tag.StartsWith("<!DOCTYPE"u8) || IsNameStart(next))
                return new TreeNode(at, at, -1, IsContainer: true, (byte)XmlNodeKind.Element);

            hasText = true;
            if (tag.StartsWith("<![CDATA["u8))
            {
                var cdata = XmlScanResume.CData(markup);
                XmlStructuralScanner.CDataEnd(source, ref cdata, out scan);
            }
            else
            {
                // A '<' that starts nothing: the index carries on as if it were text.
                scan = markup + (next == (byte)'!' ? 2 : 1);
            }
        }
    }

    /// <summary>The next <c>&lt;</c> at or after <paramref name="from"/>, noting in
    /// <paramref name="hasText"/> whether any non-whitespace lay before it. At the end of the data
    /// it returns that end, encoded as <c>-end - 1</c>.</summary>
    private long NextMarkup(long from, ref bool hasText)
    {
        long at = from;
        while (true)
        {
            var span = source.GetContiguousSpan(at, ReadStride);
            if (span.IsEmpty)
                return -at - 1;

            int lt = span.IndexOf((byte)'<');
            var text = lt < 0 ? span : span.Slice(0, lt);
            if (!hasText && text.IndexOfAnyExcept(XmlStructuralScanner.Whitespace) >= 0)
                hasText = true;

            if (lt >= 0)
                return at + lt;

            at += span.Length;
        }
    }

    private static TreeNode Leaf(long at, long end, bool hasText)
        => new(at, at, end, IsContainer: false, (byte)(hasText ? XmlNodeKind.TextElement : XmlNodeKind.EmptyElement));

    /// <summary>The XML declaration is a <c>&lt;?xml</c> instruction at the very start of the
    /// document - after a UTF-8 byte order mark, which the index does not count as text.</summary>
    private bool IsXmlDeclaration(long at)
    {
        if (at != 0 && !(at == 3 && HasByteOrderMark()))
            return false;

        Span<byte> head = stackalloc byte[6];
        int copied = source.CopyTo(at, head);
        return copied >= 5 && head.Slice(0, 5).SequenceEqual("<?xml"u8)
            && (copied == 5 || head[5] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'?');
    }

    private bool HasByteOrderMark()
    {
        Span<byte> head = stackalloc byte[3];
        return source.CopyTo(0, head) == 3 && head.SequenceEqual("﻿"u8);
    }

    private long SkipWhitespace(long at)
    {
        // The byte order mark is not text; at the document's start it is stepped over.
        if (at == 0 && HasByteOrderMark())
            at = 3;

        while (true)
        {
            var span = source.GetContiguousSpan(at, ReadStride);
            if (span.IsEmpty)
                return at;

            int content = span.IndexOfAnyExcept(XmlStructuralScanner.Whitespace);
            if (content >= 0)
                return at + content;

            at += span.Length;
        }
    }

    private int Peek(long at) => at < source.AvailableLength ? source.ByteAt(at) : -1;

    private static bool IsNameStart(int b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
        or (byte)'_' or (byte)':' or >= 0x80;
}
