namespace Argonaut.Features.Json.Schema;

/// <summary>
/// One labelled member of an enumeration: the value as it appears in the document (raw text,
/// unquoted for strings) plus the documentation the schema attaches to that specific value.
/// Built from either <c>enum</c> + the OpenAPI-conventional <c>x-enumNames</c>/
/// <c>x-enumDescriptions</c> sibling arrays, or a <c>oneOf</c>/<c>anyOf</c> whose branches are
/// all <c>const</c>.
/// </summary>
public readonly record struct EnumLabel(string ValueText, string? Title, string? Description);

/// <summary>
/// One node of a flattened <see cref="JsonSchemaDocument"/>. Every cross-reference is an
/// <c>int</c> node id (-1 = none) rather than an object reference, so a recursive <c>$ref</c>
/// costs nothing and nothing has to recurse while the document tree is being walked.
///
/// Mutable during loading only (<see cref="JsonSchemaLoader"/> patches refs and merges
/// <c>allOf</c> branches after the first pass); treated as immutable once the owning
/// <see cref="JsonSchemaDocument"/> exists.
/// </summary>
internal sealed class JsonSchemaNode
{
    public string? Title;
    public string? Description;

    /// <summary>Schema for every array element not covered by <see cref="PrefixItemIds"/>.</summary>
    public int ItemsId = -1;

    /// <summary>Schema for object members not named in <see cref="PropertyKeysUtf8"/>.</summary>
    public int AdditionalPropertiesId = -1;

    /// <summary>Positional array-slot schemas (<c>prefixItems</c>, or draft-07's array-form
    /// <c>items</c>). Null when the schema has none.</summary>
    public int[]? PrefixItemIds;

    /// <summary>Member names as UTF-8, sorted ordinally so <see cref="JsonSchemaDocument.ResolveMember"/>
    /// can binary-search a property-key span straight off the mapping with no allocation.
    /// Parallel to <see cref="PropertyNodeIds"/>.</summary>
    public byte[][]? PropertyKeysUtf8;

    public int[]? PropertyNodeIds;

    public EnumLabel[]? EnumLabels;
}
