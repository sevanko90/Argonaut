using System;
using System.Buffers;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Collections;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Xml.Indexing;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// The text an XML tree row shows, decoded from the bytes on demand: a start tag, an element's
/// text, a comment, a collapsed element's child count. Every decode is capped at
/// <see cref="DisplayText.MaxLength"/> so one pathological tag or text node (a 20 MB coordinate
/// list) cannot stall layout; a capped one reports it so the row can say so.
///
/// Markup is shown as written - quotes, prefixes and entity references kept, so a search
/// highlight lines up with the bytes - except that a row is one line, so a run of whitespace that
/// holds a line break or a tab shows as one space. Text is trimmed.
/// </summary>
public sealed class XmlTreeText(IByteSource bytes, SparseContainerIndex index, XmlTreeReader reader)
{
    /// <summary>Children counted before a summary settles for "N+": a small element is counted by
    /// reading it, and one the index has not recorded yet may not be small.</summary>
    private const int ChildCountCap = 50_000;

    // An element's child count never changes once it has closed, so entries never need
    // invalidating; bounded because a long session scrolls past a great many elements.
    private readonly LruCache<long, long> childCounts = new(10_000);

    public IByteSource Bytes => bytes;

    /// <summary>The start tag whose <c>&lt;</c> is at <paramref name="start"/> as shown: name and
    /// attributes as written, capped. <paramref name="tagEnd"/> is one past its <c>&gt;</c>, which
    /// is where its content begins.</summary>
    public string StartTag(long start, out long tagEnd, out bool truncated)
    {
        XmlStructuralScanner.TagEnd(bytes, start, out tagEnd);
        return Markup(start, tagEnd, out truncated);
    }

    /// <summary>Whether the start tag that ended at <paramref name="tagEnd"/> closes itself.</summary>
    public bool IsSelfClosing(long tagEnd) => XmlStructuralScanner.IsSelfClosing(bytes, tagEnd);

    /// <summary>Where the close tag of the element at <paramref name="start"/>, which ends at
    /// <paramref name="end"/>, begins - and so where its text content ends.</summary>
    public long CloseStart(long start, long end) => reader.CloseStart(start, end);

    /// <summary>The qualified name of the element whose start tag begins at
    /// <paramref name="start"/>, as written.</summary>
    public string ElementName(long start)
    {
        long at = start + 1;
        while (true)
        {
            var span = bytes.GetContiguousSpan(at, 256);
            if (span.IsEmpty)
                break;

            int stop = span.IndexOfAny(XmlStructuralScanner.NameEnd);
            if (stop >= 0)
            {
                at += stop;
                break;
            }

            at += span.Length;
        }

        return DisplayText.Read(bytes, start + 1, (int)Math.Min(int.MaxValue, at - start - 1), out _);
    }

    /// <summary>The bytes <paramref name="start"/> to <paramref name="end"/> as shown, capped.</summary>
    public string Markup(long start, long end, out bool truncated)
        => Fold(DisplayText.Read(bytes, start, (int)Math.Min(int.MaxValue, end - start), out truncated));

    /// <summary>
    /// The text between <paramref name="start"/> and <paramref name="end"/> with the whitespace at
    /// both ends left out, as shown and capped. The content's offset and full length - trimmed,
    /// before the cap - are reported for a "view in raw" link.
    /// </summary>
    public string Content(long start, long end, out bool truncated, out long contentOffset, out long fullLength)
    {
        contentOffset = SkipWhitespace(start, end);
        long last = end;
        while (last > contentOffset && IsWhitespace(bytes.ByteAt(last - 1)))
            last--;

        fullLength = last - contentOffset;
        return Fold(DisplayText.Read(bytes, contentOffset, (int)Math.Min(int.MaxValue, fullLength), out truncated));
    }

    /// <summary>How many children a collapsed element has, as its row's note - "1 child",
    /// "26 children", "50000+ children" - or null while the count is not known yet.</summary>
    public string? ChildCountLabel(long containerStart)
    {
        long count = ChildCount(containerStart);
        if (count < 0)
            return null;

        return count > ChildCountCap ? $"{ChildCountCap}+ children" : $"{count} {(count == 1 ? "child" : "children")}";
    }

    /// <summary>The element's child count: recorded by the index for a large closed one, counted
    /// by reading for a small one, -1 while it is still arriving. Past <see cref="ChildCountCap"/>,
    /// the cap plus one.</summary>
    public long ChildCount(long containerStart)
    {
        if (childCounts.TryGetValue(containerStart, out long cached))
            return cached;

        int record = index.FindContainerStartingAt(containerStart);
        if (record >= 0)
        {
            var container = index.GetContainer(record);
            if (container.IsOpen)
                return -1;

            childCounts.Set(containerStart, container.ChildCount);
            return container.ChildCount;
        }

        long position = reader.FirstChildPosition(containerStart);
        long count = 0;
        while (count <= ChildCountCap && reader.TryReadChild((byte)XmlNodeKind.Element, ref position, out var child, out _))
        {
            count++;
            position = child.IsContainer ? reader.SkipValue(child.ValueStart) : child.ValueEnd;
            if (position == long.MaxValue)
                return -1; // a child still arriving: the count is not known yet
        }

        if (!bytes.LengthSettled && position >= bytes.AvailableLength)
            return -1;

        childCounts.Set(containerStart, count);
        return count;
    }

    /// <summary>Where <paramref name="node"/> ends: a leaf's recorded end, a large element's end
    /// from the index, or a small one's by scanning it - <c>long.MaxValue</c> for an element still
    /// arriving.</summary>
    public long End(TreeNode node)
    {
        if (!node.IsContainer)
            return node.ValueEnd;

        int record = index.FindContainerStartingAt(node.ValueStart);
        return record >= 0 && index.GetContainer(record).End is var end and >= 0 ? end : reader.SkipValue(node.ValueStart);
    }

    /// <summary>The first byte from <paramref name="start"/> that is not whitespace, or
    /// <paramref name="end"/>.</summary>
    private long SkipWhitespace(long start, long end)
    {
        long at = start;
        while (at < end)
        {
            var span = bytes.GetContiguousSpan(at, (int)Math.Min(4096, end - at));
            if (span.IsEmpty)
                break;

            int content = span.IndexOfAnyExcept(XmlStructuralScanner.Whitespace);
            if (content >= 0)
                return at + content;

            at += span.Length;
        }

        return Math.Min(at, end);
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static readonly SearchValues<char> LineBreaksAndTabs = SearchValues.Create("\r\n\t");

    /// <summary>A row is one line: each run of whitespace holding a line break or a tab becomes one
    /// space. Returns <paramref name="text"/> itself when there is none, which is nearly always.</summary>
    internal static string Fold(string text)
    {
        if (text.AsSpan().IndexOfAny(LineBreaksAndTabs) < 0)
            return text;

        char[] folded = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            int length = 0;
            for (int i = 0; i < text.Length;)
            {
                if (text[i] is not (' ' or '\t' or '\r' or '\n'))
                {
                    folded[length++] = text[i++];
                    continue;
                }

                int runStart = i;
                bool breaks = false;
                while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
                {
                    breaks |= text[i] != ' ';
                    i++;
                }

                if (breaks)
                {
                    folded[length++] = ' ';
                }
                else
                {
                    text.CopyTo(runStart, folded, length, i - runStart);
                    length += i - runStart;
                }
            }

            return new string(folded, 0, length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(folded);
        }
    }
}
