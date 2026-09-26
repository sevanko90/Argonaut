using Argonaut.Engine.Collections;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Features.Json.Tree;

/// <summary>
/// Which node of the bound schema describes a tree row. Resolution runs top-down, as it must (see
/// <see cref="JsonSchemaDocument"/>): a row's node is one step - by name or by index - from its
/// parent container's node, and a container's node is found the same way from its own parent. A
/// row knows its parent's start, so each container is looked up once, by seeking to it, and
/// cached; a screen of siblings then costs one step each.
/// </summary>
public sealed class JsonSchemaResolver(SparseContainerIndex index, JsonTreeReader reader, JsonTreeText text)
{
    private readonly LruCache<long, int> containerNodes = new(10_000);
    private JsonSchemaDocument? schema;

    /// <summary>The bound schema, or null. Changing it forgets every resolved node.</summary>
    public JsonSchemaDocument? Schema
    {
        get => schema;
        set
        {
            schema = value;
            containerNodes.Clear();
        }
    }

    /// <summary>The schema node describing <paramref name="row"/>, or -1 when no schema is
    /// bound, the row is a close row, or the schema says nothing about this position.</summary>
    public int NodeFor(in TreeRow row)
    {
        if (schema is null || row.Shape == TreeRowShape.Close)
            return -1;

        if (row.ParentStart < 0)
            return row.Ordinal == 0 ? schema.RootId : -1;

        int parent = NodeForContainer(row.ParentStart);
        if (parent < 0)
            return -1;

        return row.ParentKind == (byte)JsonTokenKind.StartArray
            ? schema.ResolveElement(parent, (int)row.Ordinal)
            : schema.ResolveMember(parent, text.NameBytes(row));
    }

    private int NodeForContainer(long containerStart)
    {
        if (containerNodes.TryGetValue(containerStart, out int cached))
            return cached;

        // Expansion does not matter for reaching a container by its own start, so seek through
        // a state where everything is open.
        var cursor = new TreeCursor(index, reader, new TreeExpandState(int.MaxValue));
        int node = cursor.SeekTo(containerStart) && cursor.Current.Node.ValueStart == containerStart
            ? NodeFor(cursor.Current)
            : -1;

        containerNodes.Set(containerStart, node);
        return node;
    }
}
