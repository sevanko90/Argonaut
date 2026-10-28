using System.Collections.Generic;
using System.Linq;
using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Paths;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Tree;

/// <summary>
/// JSONPaths for the JSON tree, in <see cref="JsonPathSyntax"/>'s grammar: built from a row and
/// its ancestors, which the cursor already holds, and resolved by reading down from the root one
/// container at a time - a child by index through the sparse index's resume points, a member by
/// name by reading the container's names. Neither touches anything outside the containers on the
/// path.
/// </summary>
public static class JsonTreePaths
{
    /// <summary>The path to the cursor's row, a segment per level. A close row's path is its
    /// container's.</summary>
    public static IReadOnlyList<TreePathSegment> Segments(TreeCursor cursor, JsonTreeText text)
    {
        var chain = cursor.Ancestors.ToList();
        var current = cursor.Current;
        chain.Add(current.Shape == TreeRowShape.Close ? current with { Shape = TreeRowShape.Open, Start = current.Node.RowStart } : current);

        var segments = new List<TreePathSegment>(chain.Count);
        for (int i = 0; i < chain.Count; i++)
        {
            var row = chain[i];
            if (i == 0)
            {
                segments.Add(new TreePathSegment("$", row.Start));
                continue;
            }

            string label = row.ParentKind == (byte)JsonTokenKind.StartArray
                ? $"[{row.Ordinal}]"
                : JsonPathSyntax.FormatMember(DecodedName(row, text));
            segments.Add(new TreePathSegment(label[0] == '[' ? label : "." + label, row.Start));
        }

        return segments;
    }

    /// <summary>A member's name with its escapes decoded, as a path spells it.</summary>
    private static string DecodedName(in TreeRow row, JsonTreeText text)
    {
        long start = row.Node.RowStart + 1;
        return JsonPathSyntax.DecodeName(text.Bytes, start, (int)(text.NameEnd(row) - 1 - start));
    }

    public static string Format(IReadOnlyList<TreePathSegment> segments)
    {
        var path = new StringBuilder();
        foreach (var segment in segments)
            path.Append(segment.Label);
        return path.ToString();
    }

    public static TreePathResult Resolve(SparseContainerIndex index, JsonTreeReader reader, JsonTreeText text, string path)
    {
        if (!JsonPathSyntax.TryParse(path, out var segments, out string? parseError))
            return new TreePathResult(null, parseError);

        long position = 0;
        if (!reader.TryReadChild(JsonTreeReader.Document, ref position, out var current, out _))
            return new TreePathResult(null, "File is empty.");

        for (int s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            var kind = (JsonTokenKind)current.FormatKind;
            if (segment.IsArrayIndex && kind != JsonTokenKind.StartArray)
                return new TreePathResult(null,
                    $"{JsonPathSyntax.FormatPath(segments, s)} is {JsonPathSyntax.DescribeKind(kind)}, not an array - can't index into it with [{segment.ArrayIndex}].");
            if (!segment.IsArrayIndex && kind != JsonTokenKind.StartObject)
                return new TreePathResult(null,
                    $"{JsonPathSyntax.FormatPath(segments, s)} is {JsonPathSyntax.DescribeKind(kind)}, not an object - can't look up member '{segment.Name}'.");

            var found = segment.IsArrayIndex
                ? FindElement(index, reader, text, current, segment.ArrayIndex)
                : FindMember(reader, text, current, Encoding.UTF8.GetBytes(segment.Name!));
            if (found is not { } next)
            {
                string label = segment.IsArrayIndex ? $"[{segment.ArrayIndex}]" : $".{JsonPathSyntax.FormatMember(segment.Name!)}";
                return new TreePathResult(null, $"No {label} found under {JsonPathSyntax.FormatPath(segments, s)}.");
            }

            current = next;
        }

        return new TreePathResult(current.RowStart, null);
    }

    /// <summary>Element <paramref name="ordinal"/> of an array, read from the nearest resume
    /// point before it.</summary>
    private static TreeNode? FindElement(SparseContainerIndex index, JsonTreeReader reader, JsonTreeText text, TreeNode array, long ordinal)
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

            position = text.End(child);
            if (position == long.MaxValue)
                return null;
        }

        return null;
    }

    /// <summary>The member of an object whose name, unescaped, is <paramref name="nameUtf8"/>.</summary>
    private static TreeNode? FindMember(JsonTreeReader reader, JsonTreeText text, TreeNode obj, byte[] nameUtf8)
    {
        long position = reader.FirstChildPosition(obj.ValueStart);
        while (reader.TryReadChild((byte)JsonTokenKind.StartObject, ref position, out var child, out _))
        {
            var asRow = new TreeRow(child.IsContainer ? TreeRowShape.Open : TreeRowShape.Leaf, child, child.RowStart, 0, 0,
                (byte)JsonTokenKind.StartObject, obj.ValueStart, IsExpanded: false);
            if (JsonUnescape.EqualsDecodedUtf8(text.NameBytes(asRow), nameUtf8))
                return child;

            position = text.End(child);
            if (position == long.MaxValue)
                return null;
        }

        return null;
    }
}
