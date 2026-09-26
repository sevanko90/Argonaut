using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Features.Json.Paths;

/// <summary>
/// The JSONPath grammar the tree writes and reads: <c>$</c>, then any mix of <c>.name</c>,
/// <c>['name']</c> for a name that is not a bare identifier, and <c>[N]</c> for an array element -
/// e.g. <c>$.foo.bar[3]['weird key']</c>. Parsing and formatting only; walking a document by a
/// path is <c>JsonTreePaths</c>'.
/// </summary>
public static class JsonPathSyntax
{
    private static readonly Regex BareIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>One step of a parsed path: a member by name, or an array element by index.</summary>
    internal readonly record struct Segment(bool IsArrayIndex, string? Name, int ArrayIndex);

    /// <summary>A member's segment without its leading dot: the name itself when it is a bare
    /// identifier, else <c>['name']</c> with <c>\</c> and <c>'</c> escaped.</summary>
    internal static string FormatMember(string name)
        => BareIdentifier.IsMatch(name) ? name : $"['{name.Replace("\\", "\\\\").Replace("'", "\\'")}']";

    /// <summary>The first <paramref name="count"/> segments as a path.</summary>
    internal static string FormatPath(IReadOnlyList<Segment> segments, int count)
    {
        var sb = new StringBuilder("$");
        for (int i = 0; i < count; i++)
        {
            var segment = segments[i];
            if (segment.IsArrayIndex)
            {
                sb.Append('[').Append(segment.ArrayIndex).Append(']');
                continue;
            }

            string formatted = FormatMember(segment.Name!);
            sb.Append(formatted.StartsWith('[') ? formatted : "." + formatted);
        }

        return sb.ToString();
    }

    /// <summary>What a value is, as an error message names it.</summary>
    internal static string DescribeKind(JsonTokenKind kind) => kind switch
    {
        JsonTokenKind.StartObject => "an object",
        JsonTokenKind.StartArray => "an array",
        JsonTokenKind.String => "a string",
        JsonTokenKind.Number => "a number",
        JsonTokenKind.True or JsonTokenKind.False => "a boolean",
        JsonTokenKind.Null => "null",
        _ => kind.ToString()
    };

    /// <summary>A name's text with its escapes decoded - what a path spells it as.</summary>
    internal static string DecodeName(IByteSource bytes, long offset, int length)
    {
        if (length <= 0)
            return string.Empty;

        var raw = bytes.RequireContiguous(offset, length);
        if (JsonUnescape.IsPlain(raw))
            return Encoding.UTF8.GetString(raw);

        // Allocate only the final string. Even enormous escaped names use fixed scratch
        // space: count first, then decode directly into the string's character storage.
        int charCount = JsonUnescape.DecodeUtf16(raw, default);
        return string.Create(charCount, (bytes, offset, length), static (destination, source) =>
            JsonUnescape.DecodeUtf16(source.bytes.RequireContiguous(source.offset, source.length), destination));
    }

    /// <summary>
    /// Parses the dot/bracket JSONPath grammar the tree writes: an
    /// optional leading <c>$</c>, then any mix of <c>.name</c>, <c>['name']</c>/<c>["name"]</c>
    /// (with <c>\\</c>/<c>\'</c>/<c>\"</c> escaping), and <c>[N]</c> array-index segments.
    /// </summary>
    internal static bool TryParse(string path, out List<Segment> segments, out string? error)
    {
        segments = new List<Segment>();
        error = null;

        string s = path.Trim();
        if (s.Length == 0)
        {
            error = "Enter a JSONPath, e.g. $.foo.bar[0].";
            return false;
        }

        int i = s[0] == '$' ? 1 : 0;

        while (i < s.Length)
        {
            char c = s[i];
            if (c == '.')
            {
                i++;
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                    i++;

                if (i == start)
                {
                    error = $"Expected a property name after '.' at position {i}.";
                    return false;
                }

                segments.Add(new Segment(false, s[start..i], 0));
            }
            else if (c == '[')
            {
                i++;
                if (i >= s.Length)
                {
                    error = "Unterminated '[' - expected an index or a quoted key.";
                    return false;
                }

                if (s[i] is '\'' or '"')
                {
                    char quote = s[i];
                    i++;
                    var name = new StringBuilder();
                    bool closed = false;

                    while (i < s.Length)
                    {
                        char ch = s[i];
                        if (ch == '\\' && i + 1 < s.Length)
                        {
                            name.Append(s[i + 1]);
                            i += 2;
                            continue;
                        }

                        if (ch == quote)
                        {
                            closed = true;
                            i++;
                            break;
                        }

                        name.Append(ch);
                        i++;
                    }

                    if (!closed)
                    {
                        error = "Unterminated quoted key in '[...]'.";
                        return false;
                    }

                    if (i >= s.Length || s[i] != ']')
                    {
                        error = $"Expected ']' at position {i}.";
                        return false;
                    }

                    i++;
                    segments.Add(new Segment(false, name.ToString(), 0));
                }
                else
                {
                    int start = i;
                    while (i < s.Length && char.IsDigit(s[i]))
                        i++;

                    if (i == start || i >= s.Length || s[i] != ']' || !int.TryParse(s[start..i], out int arrayIndex))
                    {
                        error = $"Expected a valid array index inside '[' at position {start}.";
                        return false;
                    }

                    i++;
                    segments.Add(new Segment(true, null, arrayIndex));
                }
            }
            else
            {
                error = $"Unexpected character '{c}' at position {i}.";
                return false;
            }
        }

        return true;
    }
}
