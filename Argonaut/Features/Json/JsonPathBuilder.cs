using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// One clickable piece of a JSONPath, e.g. <c>.nested</c> or <c>[2]</c> (the root segment's
/// label is <c>$</c>). <see cref="TokenIndex"/> is always an ancestor of (or equal to) the
/// token the path was built for, so navigating to it never touches more of the document
/// than the ancestor chain itself.
/// </summary>
public readonly record struct JsonPathSegment(string Label, int TokenIndex);

/// <summary>
/// Builds a JSONPath string (e.g. <c>$.foo.bar[3]['weird key']</c>) for a token in a
/// <see cref="JsonStructureIndex"/> by walking its ParentIndex chain to the root. Only
/// touches ancestors of the selected token - never the whole document - so this stays
/// cheap (O(depth) plus, for array elements, an O(preceding-siblings) sibling-skip walk
/// per ancestor) regardless of overall file size, and runs once per selection change
/// rather than per frame.
/// </summary>
public static class JsonPathBuilder
{
    private static readonly Regex BareIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static string Build(JsonStructureIndex index, MMapFile mmap, int tokenIndex)
    {
        var segments = BuildSegments(index, mmap, tokenIndex);
        var sb = new StringBuilder();
        foreach (var segment in segments)
            sb.Append(segment.Label);

        return sb.ToString();
    }

    public static IReadOnlyList<JsonPathSegment> BuildSegments(JsonStructureIndex index, MMapFile mmap, int tokenIndex)
    {
        var raw = new List<(string Text, int TokenIndex)>();
        int current = tokenIndex;

        while (true)
        {
            var token = index.GetToken(current);
            int parentIndex = token.ParentIndex;
            if (parentIndex == -1)
            {
                raw.Add(("$", current));
                break;
            }

            string text = token.NameLength >= 0
                ? FormatMemberSegment(ReadText(mmap, token.NameOffset, token.NameLength))
                : $"[{FindArrayIndex(index, parentIndex, current)}]";

            raw.Add((text, current));
            current = parentIndex;
        }

        raw.Reverse();

        var segments = new List<JsonPathSegment>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            string text = raw[i].Text;
            string label = i == 0 || (text.Length > 0 && text[0] == '[') ? text : "." + text;
            segments.Add(new JsonPathSegment(label, raw[i].TokenIndex));
        }

        return segments;
    }

    /// <summary>
    /// Finds the zero-based index of <paramref name="targetTokenIndex"/> among its parent
    /// array's direct children by walking forward from the first child, skipping whole
    /// sibling subtrees in O(1) via each sibling's EndIndex - the same pattern
    /// <see cref="JsonVisibleRowCollection.DescribeChildCount"/> uses for child counts.
    /// </summary>
    private static int FindArrayIndex(JsonStructureIndex index, int parentIndex, int targetTokenIndex)
    {
        int i = parentIndex + 1;
        int arrayIndex = 0;

        while (i < targetTokenIndex)
        {
            var sibling = index.GetToken(i);
            i = IsContainer(sibling.Kind) ? sibling.EndIndex + 1 : i + 1;
            arrayIndex++;
        }

        return arrayIndex;
    }

    private static string FormatMemberSegment(string name)
    {
        if (BareIdentifier.IsMatch(name))
            return name;

        string escaped = name.Replace("\\", "\\\\").Replace("'", "\\'");
        return $"['{escaped}']";
    }

    private static string ReadText(MMapFile mmap, long offset, int length)
    {
        if (length <= 0)
            return string.Empty;

        return mmap.GetUtf8String(offset, length);
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;
}
