using System;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Collections;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Features.Json.Tree;

/// <summary>
/// The text a JSON tree row shows, decoded from the bytes on demand: a member's name, a scalar's
/// value, a container's summary. Every decode is capped at <see cref="DisplayText.MaxLength"/> so
/// one pathological token cannot stall layout; a capped one reports it so the row can say so.
/// </summary>
public sealed class JsonTreeText(IByteSource bytes, SparseContainerIndex index, JsonTreeReader reader)
{
    /// <summary>Children counted before a summary settles for "N+": a small container is counted
    /// by reading it, and one the index has not recorded yet may not be small.</summary>
    private const int ChildCountCap = 50_000;

    // A container's child count never changes once it has closed, so entries never need
    // invalidating; bounded because a long session scrolls past a great many containers.
    private readonly LruCache<long, long> childCounts = new(10_000);

    public IByteSource Bytes => bytes;

    /// <summary>Where a member's name ends - one past its closing quote.</summary>
    public long NameEnd(in TreeRow row) => reader.StringEnd(row.Node.RowStart);

    /// <summary>The raw bytes of a member's name between its quotes, or empty for a row without
    /// one. For matching against a schema, not for display.</summary>
    public ReadOnlySpan<byte> NameBytes(in TreeRow row)
    {
        if (!HasName(row))
            return ReadOnlySpan<byte>.Empty;

        long start = row.Node.RowStart + 1;
        return bytes.RequireContiguous(start, (int)(NameEnd(row) - 1 - start));
    }

    public static bool HasName(in TreeRow row)
        => row.ParentKind == (byte)JsonTokenKind.StartObject && row.Shape != TreeRowShape.Close;

    /// <summary>A member's name as shown, escapes kept as written; null for a row without one.</summary>
    public string? Name(in TreeRow row, out bool truncated, out long fullLength)
    {
        truncated = false;
        fullLength = 0;
        if (!HasName(row))
            return null;

        long start = row.Node.RowStart + 1;
        fullLength = NameEnd(row) - 1 - start;
        return DisplayText.Read(bytes, start, (int)Math.Min(int.MaxValue, fullLength), out truncated);
    }

    /// <summary>
    /// A scalar as shown: a string in its quotes, escapes kept as written - without the closing
    /// quote when truncated, so the value visibly continues past the ellipsis. The content's
    /// offset and full length are reported for a "view in raw" link.
    /// </summary>
    public string Scalar(in TreeRow row, out bool truncated, out long contentOffset, out long fullLength)
    {
        var node = row.Node;
        switch ((JsonTokenKind)node.FormatKind)
        {
            case JsonTokenKind.String:
                contentOffset = node.ValueStart + 1;
                fullLength = node.ValueEnd - 1 - contentOffset;
                string text = DisplayText.Read(bytes, contentOffset, (int)Math.Min(int.MaxValue, fullLength), out truncated);
                return truncated ? "\"" + text : "\"" + text + "\"";
            default:
                contentOffset = node.ValueStart;
                fullLength = node.ValueEnd - node.ValueStart;
                return DisplayText.Read(bytes, contentOffset, (int)Math.Min(int.MaxValue, fullLength), out truncated);
        }
    }

    /// <summary>The raw bytes a hint provider classifies: a string's content, or a scalar's
    /// whole token.</summary>
    public ReadOnlySpan<byte> ScalarBytes(in TreeRow row, int maxLength)
    {
        var node = row.Node;
        long start = node.FormatKind == (byte)JsonTokenKind.String ? node.ValueStart + 1 : node.ValueStart;
        long end = node.FormatKind == (byte)JsonTokenKind.String ? node.ValueEnd - 1 : node.ValueEnd;
        return end - start > maxLength ? ReadOnlySpan<byte>.Empty : bytes.RequireContiguous(start, (int)(end - start));
    }

    /// <summary>An open row's text: its bracket when expanded, a summary with its child count
    /// when collapsed - "…" while the count is not known yet.</summary>
    public string Summary(in TreeRow row)
    {
        bool isObject = row.Node.FormatKind == (byte)JsonTokenKind.StartObject;
        string open = isObject ? "{" : "[";
        if (row.IsExpanded)
            return open;

        string close = isObject ? "}" : "]";
        long count = ChildCount(row.Node.ValueStart);
        if (count < 0)
            return $"{open} … {close}";

        string label = isObject ? "member" : "item";
        string counted = count > ChildCountCap ? $"{ChildCountCap}+ {label}s" : $"{count} {label}{(count == 1 ? "" : "s")}";
        return $"{open} {counted} {close}";
    }

    /// <summary>Where <paramref name="node"/>'s value ends: a scalar's recorded end, a large
    /// container's end from the index, or a small one's by scanning it - <c>long.MaxValue</c> for a
    /// container still arriving.</summary>
    public long End(TreeNode node)
    {
        if (!node.IsContainer)
            return node.ValueEnd;

        int record = index.FindContainerStartingAt(node.ValueStart);
        return record >= 0 && index.GetContainer(record).End is var end and >= 0 ? end : reader.SkipValue(node.ValueStart);
    }

    /// <summary>Whether the container starting at <paramref name="containerStart"/> has any
    /// children - read from its first bytes, so it costs nothing however large it is.</summary>
    public bool HasChildren(long containerStart, byte kind)
    {
        long position = reader.FirstChildPosition(containerStart);
        return reader.TryReadChild(kind, ref position, out _, out _);
    }

    /// <summary>The container's child count: recorded by the index for a large closed one,
    /// counted by reading for a small one, -1 while it is still arriving. Past
    /// <see cref="ChildCountCap"/>, the cap plus one.</summary>
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

        byte kind = bytes.ByteAt(containerStart) == (byte)'{' ? (byte)JsonTokenKind.StartObject : (byte)JsonTokenKind.StartArray;
        long position = reader.FirstChildPosition(containerStart);
        long count = 0;
        while (count <= ChildCountCap && reader.TryReadChild(kind, ref position, out var child, out _))
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
}
