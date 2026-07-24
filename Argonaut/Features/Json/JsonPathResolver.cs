using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Result of resolving a JSONPath string against a <see cref="JsonStructureIndex"/>: either
/// the resolved token index, or a human-readable reason resolution failed (parse error, a
/// type mismatch such as indexing into an object, or "no such member"/"index out of range").
/// </summary>
public readonly record struct JsonPathResolveResult(int? TokenIndex, string? Error);

/// <summary>
/// Resolves a JSONPath string (the same dot/bracket grammar <see cref="JsonPathBuilder"/>
/// emits, e.g. <c>$.foo.bar[3]['weird key']</c>) to a token index in a
/// <see cref="JsonStructureIndex"/>, for the toolbar's "jump to path" action.
///
/// Resolution walks the document top-down from the root, scanning each container's direct
/// children for a name/index match - the same EndIndex sibling-skip technique
/// <see cref="JsonPathBuilder.Build"/>'s FindArrayIndex and JsonVisibleRowCollection's child
/// walks use. Unlike JsonPathBuilder (which walks a single token's ParentIndex chain from
/// leaf to root), this never touches ParentIndex: the ancestor chain here is exactly the
/// sequence of containers entered on the way down, known for free as the walk proceeds.
///
/// Large files index progressively in the background, so both "has this container finished
/// (EndIndex known)" and "has indexing reached the next sibling yet" are waited on via
/// <see cref="JsonStructureIndex.WaitForTokenCountAsync"/> - the same coverage-wait pattern
/// <see cref="JsonOffsetTokenResolver.ResolveWhenCoveredAsync"/> uses for search hits.
/// </summary>
public static class JsonPathResolver
{
    // How many further tokens to wait for per retry when a container isn't finished
    // indexing yet - see JsonOffsetTokenResolver's identical constant/rationale.
    private const int CoverageWaitBatch = 4096;

    private static readonly Regex BareIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly record struct Segment(bool IsArrayIndex, string? Name, int ArrayIndex);

    // Internal-only signal for "the file closed a container's parent before closing the
    // container itself" (truncated/malformed input) - caught in ResolveAsync and turned into
    // a Result so callers never need to catch anything but cancellation.
    private sealed class MalformedIndexException(string message) : Exception(message);

    public static async Task<JsonPathResolveResult> ResolveAsync(JsonStructureIndex index, MMapFile mmap, string path, CancellationToken cancellationToken = default)
    {
        if (!TryParse(path, out var segments, out string? parseError))
            return new JsonPathResolveResult(null, parseError);

        try
        {
            return await ResolveSegmentsAsync(index, mmap, segments, cancellationToken);
        }
        catch (MalformedIndexException ex)
        {
            return new JsonPathResolveResult(null, ex.Message);
        }
    }

    private static async Task<JsonPathResolveResult> ResolveSegmentsAsync(JsonStructureIndex index, MMapFile mmap, List<Segment> segments, CancellationToken cancellationToken)
    {
        await index.WaitForTokenIndexedAsync(0);
        if (index.TokenCount == 0)
            return new JsonPathResolveResult(null, "File is empty.");

        int current = 0;
        for (int s = 0; s < segments.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segment = segments[s];
            var container = index.GetToken(current);

            bool isArrayContainer = container.Kind == JsonTokenKind.StartArray;
            bool isObjectContainer = container.Kind == JsonTokenKind.StartObject;

            if (segment.IsArrayIndex && !isArrayContainer)
                return new JsonPathResolveResult(null,
                    $"{FormatPath(segments, s)} is {DescribeKind(container.Kind)}, not an array - can't index into it with [{segment.ArrayIndex}].");
            if (!segment.IsArrayIndex && !isObjectContainer)
                return new JsonPathResolveResult(null,
                    $"{FormatPath(segments, s)} is {DescribeKind(container.Kind)}, not an object - can't look up member '{segment.Name}'.");

            int endIndex = await WaitForEndIndexAsync(index, current, cancellationToken);

            int? next = null;
            int i = current + 1;
            int position = 0;

            while (i < endIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = index.GetToken(i);

                bool isMatch = segment.IsArrayIndex
                    ? position == segment.ArrayIndex
                    : child.NameLength >= 0 && mmap.GetUtf8String(child.NameOffset, child.NameLength) == segment.Name;

                if (isMatch)
                {
                    next = i;
                    break;
                }

                i = IsContainer(child.Kind) ? await WaitForEndIndexAsync(index, i, cancellationToken) + 1 : i + 1;
                position++;
            }

            if (next is not { } resolved)
            {
                string label = segment.IsArrayIndex ? $"[{segment.ArrayIndex}]" : $".{FormatMemberName(segment.Name!)}";
                return new JsonPathResolveResult(null, $"No {label} found under {FormatPath(segments, s)}.");
            }

            current = resolved;
        }

        return new JsonPathResolveResult(current, null);
    }

    /// <summary>
    /// Waits until <paramref name="containerIndex"/>'s matching End token has been indexed
    /// (its EndIndex stops being -1), or throws if indexing finished without ever closing it
    /// (a truncated/malformed file). Needed before scanning or skipping past a container's
    /// children, since EndIndex is the only bound available for either.
    /// </summary>
    private static async Task<int> WaitForEndIndexAsync(JsonStructureIndex index, int containerIndex, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int endIndex = index.GetToken(containerIndex).EndIndex;
            if (endIndex >= 0)
                return endIndex;
            if (index.IsComplete)
                throw new MalformedIndexException("The file ended before a container was closed - it may be truncated or malformed.");

            await index.WaitForTokenCountAsync(index.TokenCount + CoverageWaitBatch);
        }
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    private static string DescribeKind(JsonTokenKind kind) => kind switch
    {
        JsonTokenKind.StartObject => "an object",
        JsonTokenKind.StartArray => "an array",
        JsonTokenKind.String => "a string",
        JsonTokenKind.Number => "a number",
        JsonTokenKind.True or JsonTokenKind.False => "a boolean",
        JsonTokenKind.Null => "null",
        _ => kind.ToString()
    };

    private static string FormatMemberName(string name) =>
        BareIdentifier.IsMatch(name) ? name : $"['{name.Replace("\\", "\\\\").Replace("'", "\\'")}']";

    private static string FormatPath(IReadOnlyList<Segment> segments, int count)
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

            string formatted = FormatMemberName(segment.Name!);
            sb.Append(formatted.StartsWith('[') ? formatted : "." + formatted);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses the dot/bracket JSONPath grammar <see cref="JsonPathBuilder"/> emits: an
    /// optional leading <c>$</c>, then any mix of <c>.name</c>, <c>['name']</c>/<c>["name"]</c>
    /// (with <c>\\</c>/<c>\'</c>/<c>\"</c> escaping), and <c>[N]</c> array-index segments.
    /// </summary>
    private static bool TryParse(string path, out List<Segment> segments, out string? error)
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
