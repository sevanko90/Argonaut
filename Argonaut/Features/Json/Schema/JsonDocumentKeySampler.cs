using System;
using System.Collections.Generic;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Reads the property names on the document's outermost object - the evidence
/// <see cref="JsonSchemaRootMatcher"/> scores schema types against.
///
/// A bounded structural walk in the same shape as <see cref="Hints.DateHintInference"/>: it hops
/// direct children by <c>EndIndex</c> (the idiom
/// <c>JsonVisibleRowCollection.AppendSubtree</c> already uses) and stops at
/// <see cref="MaxKeys"/>, so it never scans a subtree and never depends on indexing having
/// finished. Safe on a background thread - index reads and mapping spans are both read-only.
///
/// Keys are copied out of the mapping rather than handed back as spans, because the result
/// outlives the call and the mapping must stay free to be unmapped.
/// </summary>
public static class JsonDocumentKeySampler
{
    /// <summary>
    /// Past this many keys nothing further discriminates between candidate types, and the cost of
    /// looking has to stay bounded on a document the app never holds in memory.
    /// </summary>
    public const int MaxKeys = 64;

    /// <summary>
    /// The outermost object's property names, or empty when there is no object to read them from
    /// (a scalar document, an array of scalars, or indexing not yet far enough in).
    ///
    /// An array document is sampled from its first element: a file that is a list of bookings is
    /// described by the booking schema, and matching its first element is what identifies that.
    /// The caller is responsible for knowing the match then applies to the array's items rather
    /// than to its root - see <paramref name="matchedElementOfArray"/>.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadRootKeys(JsonStructureIndex index, MMapFile mmap, out bool matchedElementOfArray)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(mmap);

        matchedElementOfArray = false;

        if (index.TokenCount == 0)
            return Array.Empty<byte[]>();

        var root = index.GetToken(0);
        int containerIndex = 0;

        if (root.Kind == JsonTokenKind.StartArray)
        {
            // The first element is the array's own first child token, if it has been indexed.
            if (index.TokenCount < 2)
                return Array.Empty<byte[]>();

            var first = index.GetToken(1);
            if (first.Kind != JsonTokenKind.StartObject)
                return Array.Empty<byte[]>();

            containerIndex = 1;
            matchedElementOfArray = true;
        }
        else if (root.Kind != JsonTokenKind.StartObject)
        {
            return Array.Empty<byte[]>();
        }

        return ReadMemberNames(index, mmap, containerIndex);
    }

    /// <summary>
    /// The direct member names of the object starting at <paramref name="containerIndex"/>.
    /// Public so the per-node match affordance can score any container the user points at, not
    /// only the document root.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadMemberNames(JsonStructureIndex index, MMapFile mmap, int containerIndex)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(mmap);

        if ((uint)containerIndex >= (uint)index.TokenCount)
            return Array.Empty<byte[]>();

        var container = index.GetToken(containerIndex);
        if (container.Kind != JsonTokenKind.StartObject)
            return Array.Empty<byte[]>();

        var keys = new List<byte[]>();
        int containerEnd = container.EndIndex;
        int childIndex = containerIndex + 1;

        while (keys.Count < MaxKeys)
        {
            // A container still being indexed has EndIndex < 0; stopping at TokenCount samples
            // whatever is there so far, which is the right answer for a partially-read file.
            if (containerEnd >= 0 && childIndex >= containerEnd)
                break;
            if (childIndex >= index.TokenCount)
                break;

            var child = index.GetToken(childIndex);
            if (child.NameLength > 0)
                keys.Add(mmap.GetSpan(child.NameOffset, child.NameLength).ToArray());

            if (child.Kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray)
            {
                if (child.EndIndex < 0)
                    break; // its sibling can't be located until it closes

                childIndex = child.EndIndex + 1;
            }
            else
            {
                childIndex++;
            }
        }

        return keys;
    }
}
