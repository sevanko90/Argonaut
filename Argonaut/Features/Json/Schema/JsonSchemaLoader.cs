using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Parses a JSON Schema file into the flattened <see cref="JsonSchemaDocument"/> the row walk
/// consumes. Schemas are small (capped at <see cref="MaxSchemaBytes"/>), so unlike the document
/// itself this is an ordinary in-memory <see cref="JsonDocument"/> parse.
///
/// Every failure mode - missing file, oversized file, malformed JSON, a schema carrying nothing
/// usable - returns null rather than throwing or surfacing an error: a schema is a display aid,
/// and "nothing matched" must look the same as "no schema", never like a problem the user has
/// to deal with.
///
/// Loading runs in three passes so that walk-time resolution stays O(1):
/// <list type="number">
/// <item>create one node per schema object, registering each by its JSON pointer;</item>
/// <item>materialise local <c>$ref</c>s by copying the target's structure into the referring
/// node (cycle-safe, so recursive schemas are fine);</item>
/// <item>merge <c>allOf</c> (and structural <c>oneOf</c>/<c>anyOf</c>) branches into their
/// owning node.</item>
/// </list>
///
/// Keyword coverage: <c>title</c>, <c>description</c>, <c>properties</c>, <c>items</c>,
/// <c>prefixItems</c> (plus draft-07's array-form <c>items</c>), <c>additionalProperties</c>,
/// <c>$defs</c>/<c>definitions</c>, local <c>#/...</c> <c>$ref</c>, <c>allOf</c>,
/// <c>oneOf</c>/<c>anyOf</c>, <c>enum</c> with <c>x-enumNames</c>/<c>x-enumDescriptions</c>.
///
/// Two ways to label individual enumerated values are recognised, and they are not equals.
/// Standard JSON Schema has no keyword for annotating the members of a bare <c>enum</c> array -
/// the values are just values - so the portable way to document them is a <c>oneOf</c> of
/// <c>const</c> branches, each carrying its own <c>title</c>/<c>description</c>. That is what the
/// schemas shipped in <c>Argonaut/Schemas/</c> use. The <c>x-enumNames</c>/<c>x-enumDescriptions</c>
/// sibling arrays are a non-standard NSwag/openapi-generator convention, supported only so that
/// generated schemas a user drops into their own schema folder still produce labels.
///
/// Deliberately ignored: remote <c>$ref</c> (anything that isn't a local <c>#</c> pointer),
/// <c>patternProperties</c>, <c>if</c>/<c>then</c>/<c>else</c>, <c>$dynamicRef</c>. And
/// <c>oneOf</c>/<c>anyOf</c> structural branches are *merged*, not discriminated against the
/// actual document value - picking a branch per row would mean reading and matching values
/// during the walk, which is exactly the cost this design exists to avoid.
/// </summary>
public static class JsonSchemaLoader
{
    /// <summary>Files past this size aren't schemas anyone wrote by hand and would defeat the
    /// "schemas fit in memory" premise; they're rejected rather than loaded.</summary>
    public const int MaxSchemaBytes = 8 * 1024 * 1024;

    // Guards against a pathological (or hostile) schema: deep nesting would otherwise recurse
    // the loader stack, and a generated schema could in principle enumerate a huge node set.
    private const int MaxDepth = 200;
    private const int MaxNodes = 200_000;

    /// <summary>Loads off the UI thread - see class remarks for why this never throws.</summary>
    public static Task<JsonSchemaDocument?> LoadFileAsync(string path) => Task.Run(() => TryLoadFile(path));

    public static JsonSchemaDocument? TryLoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSchemaBytes)
                return null;

            return TryParse(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static JsonSchemaDocument? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            return new Builder().Build(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private sealed class Builder
    {
        private static readonly IComparer<byte[]> Utf8KeyComparer =
            Comparer<byte[]>.Create((a, b) => ((ReadOnlySpan<byte>)a).SequenceCompareTo(b));

        private readonly List<JsonSchemaNode> nodes = new();

        // Parallel to nodes, and loader-only state: the local pointer this node defers to
        // (pass 2), and the allOf/oneOf/anyOf branches merged into it (pass 3).
        private readonly List<string?> refTargets = new();
        private readonly List<List<int>?> branches = new();

        private readonly Dictionary<string, int> nodesByPointer = new(StringComparer.Ordinal);

        public JsonSchemaDocument? Build(JsonElement root)
        {
            int rootId = CreateNode(root, "#", depth: 0);
            if (rootId < 0)
                return null;

            var state = new byte[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                MaterialiseRef(i, state);

            state = new byte[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                MergeBranches(i, state);

            return new JsonSchemaDocument(nodes.ToArray(), rootId);
        }

        /// <summary>
        /// Pass 1. Creates the node for one schema object and recurses into every subschema
        /// position we understand. Returns -1 for anything that isn't an object (notably the
        /// boolean schemas <c>true</c>/<c>false</c>, which carry no documentation), or when a
        /// guard limit is hit.
        /// </summary>
        private int CreateNode(JsonElement element, string pointer, int depth)
        {
            if (element.ValueKind != JsonValueKind.Object || depth > MaxDepth || nodes.Count >= MaxNodes)
                return -1;

            var node = new JsonSchemaNode();
            int id = nodes.Count;
            nodes.Add(node);
            refTargets.Add(null);
            branches.Add(null);

            // Registered before recursing so a schema that refs its own location resolves.
            nodesByPointer.TryAdd(pointer, id);

            if (element.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                node.Title = title.GetString();
            if (element.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                node.Description = description.GetString();

            if (element.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
                refTargets[id] = NormalisePointer(reference.GetString());

            ReadProperties(element, node, pointer, depth);
            ReadArrayShape(element, node, pointer, depth);

            if (element.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                node.AdditionalPropertiesId = CreateNode(additional, pointer + "/additionalProperties", depth + 1);

            // $defs entries are unreachable structurally but must exist as nodes for $ref to
            // find them, so they're created for their pointer registration alone.
            ReadDefinitions(element, pointer, depth, "$defs");
            ReadDefinitions(element, pointer, depth, "definitions");

            ReadEnumLabels(element, node);
            ReadCompositions(element, node, id, pointer, depth);

            return id;
        }

        private void ReadProperties(JsonElement element, JsonSchemaNode node, string pointer, int depth)
        {
            if (!element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                return;

            List<byte[]>? keys = null;
            List<int>? ids = null;

            foreach (var property in properties.EnumerateObject())
            {
                int childId = CreateNode(property.Value, pointer + "/properties/" + EscapeToken(property.Name), depth + 1);
                if (childId < 0)
                    continue;

                (keys ??= new List<byte[]>()).Add(Encoding.UTF8.GetBytes(property.Name));
                (ids ??= new List<int>()).Add(childId);
            }

            if (keys is null)
                return;

            var keyArray = keys.ToArray();
            var idArray = ids!.ToArray();
            Array.Sort(keyArray, idArray, Utf8KeyComparer);
            node.PropertyKeysUtf8 = keyArray;
            node.PropertyNodeIds = idArray;
        }

        private void ReadArrayShape(JsonElement element, JsonSchemaNode node, string pointer, int depth)
        {
            if (element.TryGetProperty("prefixItems", out var prefixItems) && prefixItems.ValueKind == JsonValueKind.Array)
                node.PrefixItemIds = CreateNodeList(prefixItems, pointer + "/prefixItems", depth);

            if (!element.TryGetProperty("items", out var items))
                return;

            if (items.ValueKind == JsonValueKind.Array)
            {
                // Draft-07 array-form items is exactly prefixItems under an older name.
                node.PrefixItemIds ??= CreateNodeList(items, pointer + "/items", depth);
            }
            else if (items.ValueKind == JsonValueKind.Object)
            {
                node.ItemsId = CreateNode(items, pointer + "/items", depth + 1);
            }
        }

        /// <summary>Creates one node per array entry, keeping -1 holes so ordinals stay aligned.</summary>
        private int[] CreateNodeList(JsonElement array, string pointer, int depth)
        {
            var ids = new int[array.GetArrayLength()];
            int i = 0;
            foreach (var entry in array.EnumerateArray())
            {
                ids[i] = CreateNode(entry, pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), depth + 1);
                i++;
            }

            return ids;
        }

        private void ReadDefinitions(JsonElement element, string pointer, int depth, string keyword)
        {
            if (!element.TryGetProperty(keyword, out var definitions) || definitions.ValueKind != JsonValueKind.Object)
                return;

            foreach (var definition in definitions.EnumerateObject())
                CreateNode(definition.Value, pointer + "/" + keyword + "/" + EscapeToken(definition.Name), depth + 1);
        }

        /// <summary>
        /// Builds the enum label table from <c>enum</c> plus the OpenAPI-conventional
        /// <c>x-enumNames</c>/<c>x-enumDescriptions</c> sibling arrays (matched positionally).
        /// An <c>enum</c> with no labels alongside it documents nothing, so it produces no table.
        /// </summary>
        private static void ReadEnumLabels(JsonElement element, JsonSchemaNode node)
        {
            if (!element.TryGetProperty("enum", out var values) || values.ValueKind != JsonValueKind.Array)
                return;

            var names = GetStringArray(element, "x-enumNames");
            var descriptions = GetStringArray(element, "x-enumDescriptions");
            if (names is null && descriptions is null)
                return;

            List<EnumLabel>? labels = null;
            int i = 0;
            foreach (var value in values.EnumerateArray())
            {
                string? text = ConstText(value);
                string? name = names is not null && i < names.Length ? names[i] : null;
                string? describe = descriptions is not null && i < descriptions.Length ? descriptions[i] : null;
                i++;

                if (text is null || (name is null && describe is null))
                    continue;

                (labels ??= new List<EnumLabel>()).Add(new EnumLabel(text, name, describe));
            }

            if (labels is not null)
                node.EnumLabels = labels.ToArray();
        }

        /// <summary>
        /// Handles <c>allOf</c>/<c>oneOf</c>/<c>anyOf</c>. A <c>oneOf</c>/<c>anyOf</c> whose
        /// branches are *all* <c>const</c> is the standard way to write a documented enumeration,
        /// so it becomes this node's label table with no structural descent at all. Anything else
        /// is treated as structural alternatives and merged (see class remarks).
        /// </summary>
        private void ReadCompositions(JsonElement element, JsonSchemaNode node, int id, string pointer, int depth)
        {
            foreach (string keyword in new[] { "allOf", "oneOf", "anyOf" })
            {
                if (!element.TryGetProperty(keyword, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
                    continue;

                if (keyword != "allOf" && TryReadConstLabels(array) is { } labels)
                {
                    node.EnumLabels ??= labels;
                    continue;
                }

                int i = 0;
                foreach (var branch in array.EnumerateArray())
                {
                    int branchId = CreateNode(branch, pointer + "/" + keyword + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), depth + 1);
                    i++;
                    if (branchId >= 0)
                        (branches[id] ??= new List<int>()).Add(branchId);
                }
            }
        }

        private static EnumLabel[]? TryReadConstLabels(JsonElement array)
        {
            var labels = new List<EnumLabel>(array.GetArrayLength());
            bool anyDocumented = false;

            foreach (var branch in array.EnumerateArray())
            {
                if (branch.ValueKind != JsonValueKind.Object ||
                    !branch.TryGetProperty("const", out var constant) ||
                    ConstText(constant) is not { } text)
                    return null; // not a pure const union - treat the whole thing structurally

                string? title = branch.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                string? description = branch.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                anyDocumented |= title is not null || description is not null;
                labels.Add(new EnumLabel(text, title, description));
            }

            return anyDocumented ? labels.ToArray() : null;
        }

        /// <summary>
        /// Pass 2. Copies a <c>$ref</c> target's structure into the referring node. The node's
        /// own title/description win, so <c>{"$ref": …, "title": "…"}</c> - the usual way to
        /// document a reused definition at its point of use - behaves as expected.
        ///
        /// <paramref name="state"/> marks nodes untouched/in-progress/done, which makes a
        /// recursive or mutually-recursive <c>$ref</c> terminate: the in-progress node is left
        /// with whatever it has, and its own id keeps working as an edge target.
        /// </summary>
        private void MaterialiseRef(int id, byte[] state)
        {
            if (state[id] != 0)
                return;

            if (refTargets[id] is not { } target)
            {
                state[id] = 2;
                return;
            }

            state[id] = 1;

            if (nodesByPointer.TryGetValue(target, out int targetId) && targetId != id)
            {
                MaterialiseRef(targetId, state);
                CopyStructure(nodes[id], nodes[targetId]);
                branches[id] ??= branches[targetId];
            }

            state[id] = 2;
        }

        /// <summary>Pass 3. Folds every structural branch into its owner, deepest-first so a
        /// branch that is itself a composition is already complete when it's folded in.</summary>
        private void MergeBranches(int id, byte[] state)
        {
            if (state[id] != 0)
                return;

            state[id] = 1;

            if (branches[id] is { } list)
            {
                foreach (int branchId in list)
                {
                    MergeBranches(branchId, state);
                    CopyStructure(nodes[id], nodes[branchId], unionProperties: true);
                }
            }

            state[id] = 2;
        }

        /// <summary>
        /// Folds <paramref name="source"/> into <paramref name="target"/> without ever
        /// overwriting something the target already has - "first wins", where the target's own
        /// keywords count as first and branches are folded in declaration order.
        /// Property maps union (with <paramref name="unionProperties"/>) rather than
        /// first-wins, since that's the whole point of an <c>allOf</c> split across branches.
        /// </summary>
        private static void CopyStructure(JsonSchemaNode target, JsonSchemaNode source, bool unionProperties = false)
        {
            target.Title ??= source.Title;
            target.Description ??= source.Description;

            if (target.ItemsId < 0)
                target.ItemsId = source.ItemsId;
            if (target.AdditionalPropertiesId < 0)
                target.AdditionalPropertiesId = source.AdditionalPropertiesId;

            target.PrefixItemIds ??= source.PrefixItemIds;
            target.EnumLabels ??= source.EnumLabels;

            if (unionProperties)
                UnionProperties(target, source);
            else if (target.PropertyKeysUtf8 is null)
            {
                target.PropertyKeysUtf8 = source.PropertyKeysUtf8;
                target.PropertyNodeIds = source.PropertyNodeIds;
            }
        }

        /// <summary>
        /// Merges two ordinally-sorted key/id pairs into fresh arrays, keeping the target's node
        /// on a key collision. Always allocates rather than mutating, because either side's
        /// arrays may be shared with the node a <c>$ref</c> was materialised from.
        /// </summary>
        private static void UnionProperties(JsonSchemaNode target, JsonSchemaNode source)
        {
            var sourceKeys = source.PropertyKeysUtf8;
            if (sourceKeys is null)
                return;

            var targetKeys = target.PropertyKeysUtf8;
            if (targetKeys is null)
            {
                target.PropertyKeysUtf8 = sourceKeys;
                target.PropertyNodeIds = source.PropertyNodeIds;
                return;
            }

            var keys = new List<byte[]>(targetKeys.Length + sourceKeys.Length);
            var ids = new List<int>(targetKeys.Length + sourceKeys.Length);
            int i = 0, j = 0;

            while (i < targetKeys.Length && j < sourceKeys.Length)
            {
                int comparison = ((ReadOnlySpan<byte>)targetKeys[i]).SequenceCompareTo(sourceKeys[j]);
                if (comparison <= 0)
                {
                    keys.Add(targetKeys[i]);
                    ids.Add(target.PropertyNodeIds![i]);
                    if (comparison == 0)
                        j++;
                    i++;
                }
                else
                {
                    keys.Add(sourceKeys[j]);
                    ids.Add(source.PropertyNodeIds![j]);
                    j++;
                }
            }

            for (; i < targetKeys.Length; i++)
            {
                keys.Add(targetKeys[i]);
                ids.Add(target.PropertyNodeIds![i]);
            }

            for (; j < sourceKeys.Length; j++)
            {
                keys.Add(sourceKeys[j]);
                ids.Add(source.PropertyNodeIds![j]);
            }

            target.PropertyKeysUtf8 = keys.ToArray();
            target.PropertyNodeIds = ids.ToArray();
        }

        private static string?[]? GetStringArray(JsonElement element, string keyword)
        {
            if (!element.TryGetProperty(keyword, out var array) || array.ValueKind != JsonValueKind.Array)
                return null;

            var values = new string?[array.GetArrayLength()];
            int i = 0;
            foreach (var entry in array.EnumerateArray())
                values[i++] = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;

            return values;
        }

        /// <summary>Renders a schema constant the way the row's decoded display text renders it
        /// (strings unquoted - <see cref="JsonSchemaDocument.TryGetEnumLabel"/> strips the
        /// display quotes before comparing). Objects and arrays can't be matched, so they're
        /// dropped.</summary>
        private static string? ConstText(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => null
        };

        /// <summary>Accepts only local pointers (<c>#</c> or <c>#/…</c>). Remote refs are
        /// ignored by design - resolving them would mean network or filesystem IO during what
        /// must stay a pure, failure-free parse.</summary>
        private static string? NormalisePointer(string? reference)
            => reference == "#" || (reference is not null && reference.StartsWith("#/", StringComparison.Ordinal))
                ? reference
                : null;

        private static string EscapeToken(string token)
            => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }
}
