using System;
using System.Collections.Generic;
using System.Globalization;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// One schema a document can be bound to *as its root*, named as the schema file names it.
/// Only ever populated from the definition containers - <c>$defs</c>, <c>definitions</c>, and
/// OpenAPI's <c>components/schemas</c> - since those are the only places a schema file holds
/// several independently-usable schemas side by side.
/// </summary>
public readonly record struct SchemaRoot(string Name, int NodeId);

/// <summary>
/// An immutable, flattened JSON Schema: a node array plus O(1)/O(log n) lookups for
/// "given the schema node for this container, what's the schema node for this child?".
///
/// The whole point of the flattening is that resolving a document row against the schema
/// happens *top-down, in lockstep with the tree walk* (see
/// <see cref="JsonVisibleRowCollection"/>'s AppendSubtree): each row inherits its parent's
/// schema node id and does one member/element lookup. Nothing ever builds a JSON path, and
/// nothing recurses into the schema at walk time - which is what makes schema hints affordable
/// on a document the app never holds in memory.
///
/// Unlike the document, a schema is small enough to hold whole, so this is a plain object
/// graph with decoded strings.
///
/// Known limits (see <see cref="JsonSchemaLoader"/>): <c>oneOf</c>/<c>anyOf</c> structural
/// branches are merged rather than discriminated against the actual value, and enum matching
/// is textual (with numeric normalisation) rather than JSON value equality.
/// </summary>
public sealed class JsonSchemaDocument
{
    private readonly JsonSchemaNode[] nodes;
    private readonly SchemaRoot[] namedRoots;

    internal JsonSchemaDocument(JsonSchemaNode[] nodes, int documentRootId, SchemaRoot[] namedRoots, bool documentRootIsUsable)
    {
        this.nodes = nodes;
        this.namedRoots = namedRoots;
        DocumentRootId = documentRootId;
        DocumentRootIsUsable = documentRootIsUsable;
        RootId = documentRootId;
    }

    /// <summary>Rebinding constructor - shares the node array, which is immutable once the
    /// owning document exists, so switching roots allocates nothing but the wrapper.</summary>
    private JsonSchemaDocument(JsonSchemaDocument source, int rootId, string? rootName)
    {
        nodes = source.nodes;
        namedRoots = source.namedRoots;
        DocumentRootId = source.DocumentRootId;
        DocumentRootIsUsable = source.DocumentRootIsUsable;
        RootId = rootId;
        RootName = rootName;
    }

    /// <summary>Node id the row walk starts from - the document root unless
    /// <see cref="WithRoot"/> has rebound it to one of <see cref="NamedRoots"/>.</summary>
    public int RootId { get; }

    /// <summary>Node id of the schema file's own root object, whether or not it's usable.</summary>
    public int DocumentRootId { get; }

    /// <summary>Which of <see cref="NamedRoots"/> is currently bound, or null for the document
    /// root. Round-tripped through <see cref="Infrastructure.SchemaSelectionPreference"/>.</summary>
    public string? RootName { get; }

    /// <summary>
    /// The independently-bindable schemas this file holds, sorted by name. Empty for an ordinary
    /// single-schema file. Non-empty for a file with <c>$defs</c>/<c>definitions</c>, and - the
    /// case this exists for - for an OpenAPI document, where <em>every</em> usable schema is a
    /// named component and the document root is not a schema at all.
    /// </summary>
    public IReadOnlyList<SchemaRoot> NamedRoots => namedRoots;

    /// <summary>
    /// Whether the schema file's own root says anything the walk could use. False for an OpenAPI
    /// document, whose root carries <c>openapi</c>/<c>info</c>/<c>paths</c> and no schema
    /// keywords - which is what tells the UI to offer <see cref="NamedRoots"/> instead of a
    /// "document root" option that would label nothing.
    /// </summary>
    public bool DocumentRootIsUsable { get; }

    /// <summary>
    /// Binds one of <see cref="NamedRoots"/> as the root the walk starts from, or the document
    /// root when <paramref name="rootName"/> is null. An unknown name binds the document root
    /// rather than failing - a schema file edited to drop a type must not break the document
    /// that remembered it.
    ///
    /// Returns <c>this</c> when nothing changes, so the caller's reference-equality check (see
    /// <see cref="JsonSchemaSettings.SetDocument"/>) correctly treats it as a no-op.
    /// </summary>
    public JsonSchemaDocument WithRoot(string? rootName)
    {
        int rootId = DocumentRootId;
        if (rootName is not null)
        {
            rootId = -1;
            foreach (var root in namedRoots)
            {
                if (string.Equals(root.Name, rootName, StringComparison.Ordinal))
                {
                    rootId = root.NodeId;
                    break;
                }
            }

            if (rootId < 0)
            {
                rootName = null;
                rootId = DocumentRootId;
            }
        }

        return rootId == RootId && rootName == RootName ? this : new JsonSchemaDocument(this, rootId, rootName);
    }

    internal int NodeCount => nodes.Length;

    /// <summary>
    /// Schema node for the object member named <paramref name="utf8Key"/> under
    /// <paramref name="parentNodeId"/>, or -1 when the schema says nothing about it.
    /// <paramref name="utf8Key"/> is the raw property-name span from the mapping, so this
    /// allocates nothing.
    ///
    /// Limit: the comparison is against the schema key's UTF-8 bytes, so a document property
    /// name written with JSON escapes (<c>"a"</c>) won't match the schema's plain
    /// <c>"a"</c>. Escaped property names are vanishingly rare and the failure mode is simply
    /// "no hint shown".
    /// </summary>
    public int ResolveMember(int parentNodeId, ReadOnlySpan<byte> utf8Key)
    {
        if ((uint)parentNodeId >= (uint)nodes.Length)
            return -1;

        var node = nodes[parentNodeId];
        var keys = node.PropertyKeysUtf8;
        if (keys is not null)
        {
            int lo = 0;
            int hi = keys.Length - 1;
            while (lo <= hi)
            {
                int mid = (int)(((uint)lo + (uint)hi) >> 1);
                int cmp = utf8Key.SequenceCompareTo(keys[mid]);
                if (cmp == 0)
                    return node.PropertyNodeIds![mid];
                if (cmp > 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
        }

        return node.AdditionalPropertiesId;
    }

    /// <summary>
    /// Schema node for the array element at <paramref name="ordinal"/> under
    /// <paramref name="parentNodeId"/>: the matching <c>prefixItems</c> slot when in range,
    /// otherwise <c>items</c>. -1 when the schema says nothing about it.
    /// </summary>
    public int ResolveElement(int parentNodeId, int ordinal)
    {
        if ((uint)parentNodeId >= (uint)nodes.Length)
            return -1;

        var node = nodes[parentNodeId];
        var prefix = node.PrefixItemIds;
        if (prefix is not null && (uint)ordinal < (uint)prefix.Length)
            return prefix[ordinal];

        return node.ItemsId;
    }

    /// <summary>
    /// This node's declared property names, ordinally sorted (empty when it declares none).
    /// Exposed for <see cref="JsonSchemaRootMatcher"/>, which merges them against the document's
    /// own keys - the array is the loader's, so callers must treat it as read-only.
    /// </summary>
    public IReadOnlyList<byte[]> GetPropertyKeys(int nodeId)
        => (uint)nodeId < (uint)nodes.Length
            ? nodes[nodeId].PropertyKeysUtf8 ?? Array.Empty<byte[]>()
            : Array.Empty<byte[]>();

    /// <summary>How many properties this node declares - shown in the root picker as a cheap
    /// way to tell a two-field wrapper from the twenty-field payload it wraps.</summary>
    public int GetPropertyCount(int nodeId)
        => (uint)nodeId < (uint)nodes.Length ? nodes[nodeId].PropertyKeysUtf8?.Length ?? 0 : 0;

    public string? GetTitle(int nodeId)
        => (uint)nodeId < (uint)nodes.Length ? nodes[nodeId].Title : null;

    public string? GetDescription(int nodeId)
        => (uint)nodeId < (uint)nodes.Length ? nodes[nodeId].Description : null;

    /// <summary>
    /// Looks up the label for one enumerated value, matching <paramref name="valueText"/> - the
    /// display text the row has already decoded - against this node's enum members. Returns
    /// false when the node has no enum table or nothing matches.
    ///
    /// Matching is textual, not JSON value equality: for <see cref="JsonTokenKind.String"/> the
    /// surrounding display quotes are stripped and the remainder compared ordinally (so a
    /// display-truncated value never matches, which is the desired outcome), and for
    /// <see cref="JsonTokenKind.Number"/> an exact text mismatch falls back to a decimal-normalised
    /// compare so <c>3</c> matches <c>3.0</c>. Escapes inside a string value are not unescaped.
    /// </summary>
    public bool TryGetEnumLabel(int nodeId, string valueText, JsonTokenKind kind, out string? title, out string? description)
    {
        title = null;
        description = null;

        if ((uint)nodeId >= (uint)nodes.Length)
            return false;

        var labels = nodes[nodeId].EnumLabels;
        if (labels is null)
            return false;

        string candidate = valueText;
        if (kind == JsonTokenKind.String)
        {
            // BuildScalarText wraps a string value in display quotes, and drops the closing
            // one when the text was truncated - which correctly fails to match here.
            if (candidate.Length < 2 || candidate[0] != '"' || candidate[^1] != '"')
                return false;
            candidate = candidate[1..^1];
        }

        foreach (var label in labels)
        {
            if (string.Equals(label.ValueText, candidate, StringComparison.Ordinal) ||
                (kind == JsonTokenKind.Number && NumbersEqual(label.ValueText, candidate)))
            {
                title = label.Title;
                description = label.Description;
                return title is not null || description is not null;
            }
        }

        return false;
    }

    private static bool NumbersEqual(string a, string b)
        => decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
            && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
            && da == db;
}
