using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

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
        var segments = new List<string>();
        int current = tokenIndex;

        while (current != -1)
        {
            var token = index.GetToken(current);
            int parentIndex = token.ParentIndex;
            if (parentIndex == -1)
                break; // root - no segment for the document root itself

            if (token.NameLength >= 0)
            {
                string name = ReadText(mmap, token.NameOffset, token.NameLength);
                segments.Add(FormatMemberSegment(name));
            }
            else
            {
                int arrayIndex = FindArrayIndex(index, parentIndex, current);
                segments.Add($"[{arrayIndex}]");
            }

            current = parentIndex;
        }

        segments.Reverse();

        var sb = new StringBuilder("$");
        foreach (var segment in segments)
        {
            if (segment.Length > 0 && segment[0] == '[')
                sb.Append(segment);
            else
                sb.Append('.').Append(segment);
        }

        return sb.ToString();
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

        return Encoding.UTF8.GetString(mmap.GetSpan(offset, length));
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;
}
