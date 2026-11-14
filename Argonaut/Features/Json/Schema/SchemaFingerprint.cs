using System;
using System.Collections.Generic;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// One type a schema can be bound as, reduced to what matching reads: its name (null for the
/// schema's own root), the node it binds, and its declared and required property names, each
/// ordinally sorted UTF-8.
/// </summary>
public readonly record struct SchemaTypeKeys(string? Name, int NodeId, IReadOnlyList<byte[]> PropertyKeys, IReadOnlyList<byte[]> RequiredKeys);

/// <summary>
/// What <see cref="JsonSchemaRootMatcher"/> needs of a schema, and nothing else: one
/// <see cref="SchemaTypeKeys"/> per bindable type - the schema's own root when it is usable, then
/// each named root.
///
/// This is what lets every schema in the catalog be held at once for matching without holding
/// every parsed schema: the key arrays are shared with the parsed document, but the node array,
/// titles, descriptions and enum labels are not kept alive by it.
/// </summary>
public sealed class SchemaFingerprint
{
    private SchemaFingerprint(SchemaTypeKeys[] types) => Types = types;

    /// <summary>The bindable types, the schema's own root first when it is a candidate.</summary>
    public IReadOnlyList<SchemaTypeKeys> Types { get; }

    public static SchemaFingerprint Of(JsonSchemaDocument schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var roots = schema.NamedRoots;
        var types = new List<SchemaTypeKeys>(roots.Count + 1);

        // The schema's own root competes with the named ones whenever it is usable - see the
        // remark on SchemaRootMatch.Name.
        if (schema.DocumentRootIsUsable)
            types.Add(TypeOf(schema, name: null, schema.DocumentRootId));

        foreach (var root in roots)
            types.Add(TypeOf(schema, root.Name, root.NodeId));

        return new SchemaFingerprint(types.ToArray());
    }

    private static SchemaTypeKeys TypeOf(JsonSchemaDocument schema, string? name, int nodeId)
        => new(name, nodeId, schema.GetPropertyKeys(nodeId), schema.GetRequiredKeys(nodeId));
}
