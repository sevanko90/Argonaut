using System;
using System.Collections.Generic;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Collections;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Features.Json;

/// <summary>
/// Builds display <see cref="JsonRow"/>s from a (index, bytes) pair, extracted verbatim from
/// JsonVisibleRowCollection so the diff view can host two of these (one per side) while the
/// JSON view keeps exactly one. Owns everything about turning a token into displayed text -
/// scalar decoding/truncation, container summaries and child counts, hints, schema labels -
/// and none of the expand/visibility state, which stays with the owning collection (that is
/// why <see cref="BuildRow"/> takes <c>expanded</c> rather than deciding it).
/// </summary>
internal sealed class JsonRowFactory
{
    // Display cap for any one decoded text (a scalar value or a property name) - see
    // DisplayText for why every display path is capped. Rows past the cap render a
    // truncation hint carrying the token's real length instead.
    internal const int MaxDisplayTextLength = DisplayText.MaxLength;

    private const int ChildCountCap = 50_000;

    // A container's direct-child count is immutable once its EndIndex is known (and
    // DescribeChildCount only runs then), so entries never need invalidating and the cache
    // intentionally survives Rebuild - without it, every collapsed container in view
    // recounts up to ChildCountCap tokens on every growth-poll rebuild. Bounded (LRU)
    // because the entries otherwise accumulate for the life of the document: the diff view
    // auto-expands changed subtrees, which exercises this far harder than manual browsing.
    private const int ChildCountCacheCapacity = 50_000;
    private readonly LruCache<int, int> childCountCache = new(ChildCountCacheCapacity);

    private readonly JsonStructureIndex index;
    private readonly IByteSource bytes;
    private readonly IReadOnlyList<IValueHintProvider>? hintProviders;

    public JsonRowFactory(JsonStructureIndex index, IByteSource bytes, IReadOnlyList<IValueHintProvider>? hintProviders)
    {
        this.index = index;
        this.bytes = bytes;
        this.hintProviders = hintProviders;
    }

    /// <summary>The bound schema, or null. Set by the owning collection alongside its own
    /// rebuild - resolution of per-row node ids happens during the collection's walk, never
    /// here (see JsonSchemaDocument's remarks for why it is top-down only).</summary>
    public JsonSchemaDocument? Schema { get; set; }

    /// <summary>
    /// Subtracted from every row's depth, so a tree rooted at something other than the document
    /// root still indents from zero - what the array table's cell pane needs, showing one cell's
    /// subtree beside the grid. Zero for a whole-document tree, which is every other caller.
    /// </summary>
    public int DepthOffset { get; init; }

    public JsonRow BuildRow(int position, int tokenIndex, int arrayIndexOrMinusOne, int schemaNodeId, bool expanded)
    {
        var token = index.GetToken(tokenIndex);
        bool nameTruncated = false;
        string? name = token.NameLength >= 0 ? ReadText(token.NameOffset, token.NameLength, out nameTruncated) : null;
        bool isContainer = IsContainer(token.Kind);

        bool valueTruncated = false;
        string value = isContainer
            ? BuildContainerSummary(tokenIndex, token, expanded)
            : BuildScalarText(token, out valueTruncated);

        bool hasChildren = isContainer && (token.EndIndex < 0 || token.EndIndex > tokenIndex + 1);

        string? hint = isContainer ? null : BuildHint(tokenIndex, token);

        string? schemaTitle = null;
        string? schemaDescription = null;
        string? schemaLabel = null;
        if (Schema is { } schema && schemaNodeId >= 0)
        {
            schemaTitle = schema.GetTitle(schemaNodeId);
            schemaDescription = schema.GetDescription(schemaNodeId);

            // Enum matching reuses the value string already decoded above - no extra bytes read,
            // no extra allocation - and a matched member label supersedes the node's own title,
            // since "Sold by third party" says more here than "Availability".
            if (!isContainer && schema.TryGetEnumLabel(schemaNodeId, value, token.Kind, out var enumTitle, out var enumDescription))
            {
                schemaTitle = enumTitle ?? schemaTitle;
                schemaDescription = enumDescription ?? schemaDescription;
            }

            // A schema that documents a property with `description` and no `title` is the norm
            // rather than the exception once schemas are generated rather than hand-written: in a
            // real OpenAPI document 83% of documented properties carry a description only. Those
            // rows would otherwise show nothing at all in the gutter - and, since the gutter cell
            // is what carries the tooltip, their description would be unreachable too.
            schemaLabel = schemaTitle ?? JsonSchemaLabel.FirstLine(schemaDescription);
        }

        string? truncationHint = valueTruncated
            ? $"(truncated — full length {FormatByteLength(token.Length)})"
            : nameTruncated
                ? $"(name truncated — full length {FormatByteLength(token.NameLength)})"
                : null;

        long? truncatedValueOffset = valueTruncated ? token.Offset : null;

        int? arrayIndex = arrayIndexOrMinusOne >= 0 ? arrayIndexOrMinusOne : null;

        // A string's token starts after its opening quote; its value starts at the quote.
        long valueStart = token.Kind == JsonTokenKind.String ? token.Offset - 1 : token.Offset;
        return new JsonRow(position, valueStart, token.Depth - DepthOffset, token.Kind, name, value, hasChildren, expanded, isPlaceholder: false, hint: hint, truncationHint: truncationHint, truncatedValueOffset: truncatedValueOffset, arrayIndex: arrayIndex, schemaTitle: schemaTitle, schemaDescription: schemaDescription, schemaLabel: schemaLabel);
    }

    private string? BuildHint(int tokenIndex, JsonTokenInfo token)
    {
        if (hintProviders is null)
            return null;

        // No classifiable value (a date in some encoding) is anywhere near this long; skip
        // early rather than hand providers a span over a pathologically large token.
        if (token.Length > MaxDisplayTextLength)
            return null;

        foreach (var provider in hintProviders)
        {
            if (!provider.IsActive)
                continue;

            if (provider.TryClassify(token.Kind, bytes.RequireContiguous(token.Offset, token.Length), out var candidate))
            {
                string? hint = provider.FormatHint(in candidate, token.Offset);
                if (hint is not null)
                    return hint;
            }
        }

        return null;
    }

    public string BuildContainerSummary(int tokenIndex, JsonTokenInfo token, bool expanded)
    {
        string open = token.Kind == JsonTokenKind.StartObject ? "{" : "[";
        if (expanded)
            return open;

        string close = token.Kind == JsonTokenKind.StartObject ? "}" : "]";
        string countText = token.EndIndex >= 0 ? DescribeChildCount(tokenIndex, token) : "…";
        return $"{open} {countText} {close}";
    }

    public string DescribeChildCount(int containerTokenIndex, JsonTokenInfo container)
    {
        string label = container.Kind == JsonTokenKind.StartObject ? "member" : "item";

        if (!childCountCache.TryGetValue(containerTokenIndex, out int count))
        {
            int i = containerTokenIndex + 1;
            int end = container.EndIndex;

            while (i < end && count <= ChildCountCap)
            {
                var t = index.GetToken(i);
                count++;
                i = IsContainer(t.Kind) ? t.EndIndex + 1 : i + 1;
            }

            childCountCache.Set(containerTokenIndex, count);
        }

        return count > ChildCountCap ? $"{ChildCountCap}+ {label}s" : $"{count} {label}{(count == 1 ? "" : "s")}";
    }

    public string BuildScalarText(JsonTokenInfo token, out bool truncated)
    {
        switch (token.Kind)
        {
            case JsonTokenKind.Null: truncated = false; return "null";
            case JsonTokenKind.True: truncated = false; return "true";
            case JsonTokenKind.False: truncated = false; return "false";
            case JsonTokenKind.EndObject: truncated = false; return "}";
            case JsonTokenKind.EndArray: truncated = false; return "]";
            case JsonTokenKind.Number: return ReadText(token.Offset, token.Length, out truncated);
            default:
                string text = ReadText(token.Offset, token.Length, out truncated);
                // A truncated string keeps its opening quote but gets no closing one: the
                // value visibly continues past the ellipsis. This also keeps copy-value's
                // quote stripping (first + last char) correct - it removes the quote and
                // the ellipsis, leaving exactly the truncated raw text.
                return truncated ? "\"" + text : "\"" + text + "\"";
        }
    }

    private string ReadText(long offset, int length, out bool truncated)
        => DisplayText.Read(bytes, offset, length, out truncated, MaxDisplayTextLength);

    private static string FormatByteLength(int bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.#} GB"
    };

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;
}
