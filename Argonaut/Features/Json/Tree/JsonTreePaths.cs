using System.Collections.Generic;
using System.Linq;
using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Paths;

namespace Argonaut.Features.Json.Tree;

/// <summary>One clickable piece of a JSONPath - <c>$</c>, <c>.nested</c>, <c>[2]</c> - and the
/// byte offset of the row it names, which revealing it lands on.</summary>
public readonly record struct JsonTreePathSegment(string Label, long Target);

/// <summary>Where a JSONPath leads: the target row's start, or why it leads nowhere.</summary>
public readonly record struct JsonTreePathResult(long? Target, string? Error);

/// <summary>
/// JSONPaths for the JSON tree, in the grammar <see cref="JsonPathBuilder"/> writes and
/// <see cref="JsonPathResolver"/> reads: built from a row and its ancestors, which the cursor
/// already holds, and resolved by reading down from the root one container at a time - a child by
/// index through the sparse index's resume points, a member by name by reading the container's
/// names. Neither touches anything outside the containers on the path.
/// </summary>
public static class JsonTreePaths
{
    /// <summary>The path to the cursor's row, a segment per level. A close row's path is its
    /// container's.</summary>
    public static IReadOnlyList<JsonTreePathSegment> Segments(TreeCursor cursor, JsonTreeText text)
    {
        var chain = cursor.Ancestors.ToList();
        var current = cursor.Current;
        chain.Add(current.Shape == TreeRowShape.Close ? current with { Shape = TreeRowShape.Open, Start = current.Node.RowStart } : current);

        var segments = new List<JsonTreePathSegment>(chain.Count);
        for (int i = 0; i < chain.Count; i++)
        {
            var row = chain[i];
            if (i == 0)
            {
                segments.Add(new JsonTreePathSegment("$", row.Start));
                continue;
            }

            string label = row.ParentKind == (byte)JsonTokenKind.StartArray
                ? $"[{row.Ordinal}]"
                : JsonPathBuilder.FormatMemberSegment(DecodedName(row, text));
            segments.Add(new JsonTreePathSegment(label[0] == '[' ? label : "." + label, row.Start));
        }

        return segments;
    }

    /// <summary>A member's name with its escapes decoded, as a path spells it.</summary>
    private static string DecodedName(in TreeRow row, JsonTreeText text)
    {
        long start = row.Node.RowStart + 1;
        return JsonPathBuilder.ReadText(text.Bytes, start, (int)(text.NameEnd(row) - 1 - start));
    }

    public static string Format(IReadOnlyList<JsonTreePathSegment> segments)
    {
        var path = new StringBuilder();
        foreach (var segment in segments)
            path.Append(segment.Label);
        return path.ToString();
    }

    public static JsonTreePathResult Resolve(SparseContainerIndex index, JsonTreeReader reader, JsonTreeText text, string path)
    {
        if (!JsonPathResolver.TryParse(path, out var segments, out string? parseError))
            return new JsonTreePathResult(null, parseError);

        long position = 0;
        if (!reader.TryReadChild(JsonTreeReader.Document, ref position, out var current, out _))
            return new JsonTreePathResult(null, "File is empty.");

        for (int s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            var kind = (JsonTokenKind)current.FormatKind;
            if (segment.IsArrayIndex && kind != JsonTokenKind.StartArray)
                return new JsonTreePathResult(null,
                    $"{JsonPathResolver.FormatPath(segments, s)} is {JsonPathResolver.DescribeKind(kind)}, not an array - can't index into it with [{segment.ArrayIndex}].");
            if (!segment.IsArrayIndex && kind != JsonTokenKind.StartObject)
                return new JsonTreePathResult(null,
                    $"{JsonPathResolver.FormatPath(segments, s)} is {JsonPathResolver.DescribeKind(kind)}, not an object - can't look up member '{segment.Name}'.");

            var found = segment.IsArrayIndex
                ? FindElement(index, reader, current, segment.ArrayIndex)
                : FindMember(index, reader, text, current, Encoding.UTF8.GetBytes(segment.Name!));
            if (found is not { } next)
            {
                string label = segment.IsArrayIndex ? $"[{segment.ArrayIndex}]" : $".{JsonPathResolver.FormatMemberName(segment.Name!)}";
                return new JsonTreePathResult(null, $"No {label} found under {JsonPathResolver.FormatPath(segments, s)}.");
            }

            current = next;
        }

        return new JsonTreePathResult(current.RowStart, null);
    }

    /// <summary>Element <paramref name="ordinal"/> of an array, read from the nearest resume
    /// point before it.</summary>
    private static TreeNode? FindElement(SparseContainerIndex index, JsonTreeReader reader, TreeNode array, long ordinal)
    {
        long position = reader.FirstChildPosition(array.ValueStart);
        long at = 0;
        int record = index.FindContainerStartingAt(array.ValueStart);
        if (record >= 0)
        {
            var resume = index.FindResumePoint(record, ordinal);
            if (!resume.AtOpen)
                (position, at) = (resume.Offset, resume.Ordinal);
        }

        while (reader.TryReadChild((byte)JsonTokenKind.StartArray, ref position, out var child, out _))
        {
            if (at++ == ordinal)
                return child;

            position = End(index, reader, child);
            if (position == long.MaxValue)
                return null;
        }

        return null;
    }

    /// <summary>The member of an object whose name, unescaped, is <paramref name="nameUtf8"/>.</summary>
    private static TreeNode? FindMember(SparseContainerIndex index, JsonTreeReader reader, JsonTreeText text, TreeNode obj, byte[] nameUtf8)
    {
        long position = reader.FirstChildPosition(obj.ValueStart);
        while (reader.TryReadChild((byte)JsonTokenKind.StartObject, ref position, out var child, out _))
        {
            var asRow = new TreeRow(child.IsContainer ? TreeRowShape.Open : TreeRowShape.Leaf, child, child.RowStart, 0, 0,
                (byte)JsonTokenKind.StartObject, obj.ValueStart, IsExpanded: false);
            if (JsonUnescape.EqualsDecodedUtf8(text.NameBytes(asRow), nameUtf8))
                return child;

            position = End(index, reader, child);
            if (position == long.MaxValue)
                return null;
        }

        return null;
    }

    private static long End(SparseContainerIndex index, JsonTreeReader reader, TreeNode node)
    {
        if (!node.IsContainer)
            return node.ValueEnd;

        int record = index.FindContainerStartingAt(node.ValueStart);
        return record >= 0 && index.GetContainer(record).End is var end and >= 0 ? end : reader.SkipValue(node.ValueStart);
    }
}
