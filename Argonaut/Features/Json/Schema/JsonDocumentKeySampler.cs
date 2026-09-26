using System;
using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Reads the property names on the document's outermost object - the evidence
/// <see cref="JsonSchemaRootMatcher"/> scores schema types against.
///
/// A bounded walk in the same shape as <see cref="Hints.DateHintInference"/>: it hops from member
/// to member - reading at most a small container through, since a large one's end is recorded -
/// and stops at <see cref="MaxKeys"/>, so it never reads a subtree and never depends on indexing
/// having finished. Safe on a background thread with its own reader and text.
///
/// Keys are copied out of the mapping rather than handed back as spans, because the result
/// outlives the call and the mapping must stay free to be unmapped.
/// </summary>
public static class JsonDocumentKeySampler
{
    /// <summary>
    /// The cost of looking has to stay bounded on a document the app never holds in memory, but
    /// the cap is not purely a cost knob: a truncated sample makes a schema key that sits past it
    /// look *absent*, which drags down the precision
    /// <see cref="JsonSchemaRootMatcher.MinimumPrecision"/> reads. So it has to clear the widest
    /// object a schema realistically describes - a live Keepa product runs to about a hundred
    /// members - rather than the narrowest one that still discriminates. Beyond this we are into
    /// objects used as maps, where no schema type is the answer anyway.
    /// </summary>
    public const int MaxKeys = 256;

    /// <summary>
    /// The outermost object's property names, or empty when there is no object to read them from
    /// (a scalar document, an array of scalars, or nothing arrived yet).
    ///
    /// An array document is sampled from its first element: a file that is a list of bookings is
    /// described by the booking schema, and matching its first element is what identifies that.
    /// The caller is responsible for knowing the match then applies to the array's items rather
    /// than to its root - see <paramref name="matchedElementOfArray"/>.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadRootKeys(JsonTreeReader reader, JsonTreeText text, out bool matchedElementOfArray)
    {
        matchedElementOfArray = false;
        long position = 0;
        if (!reader.TryReadChild(JsonTreeReader.Document, ref position, out var root, out _))
            return Array.Empty<byte[]>();

        if (root.FormatKind != (byte)JsonTokenKind.StartArray)
            return ReadMemberNames(reader, text, root);

        long first = reader.FirstChildPosition(root.ValueStart);
        if (!reader.TryReadChild(root.FormatKind, ref first, out var element, out _) || element.FormatKind != (byte)JsonTokenKind.StartObject)
            return Array.Empty<byte[]>();

        matchedElementOfArray = true;
        return ReadMemberNames(reader, text, element);
    }

    /// <summary>
    /// The direct member names of <paramref name="container"/>, up to <see cref="MaxKeys"/>; empty
    /// for anything but an object. Public so a per-node match can score any object the user points
    /// at, not only the document root. A member still arriving ends the sample.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadMemberNames(JsonTreeReader reader, JsonTreeText text, TreeNode container)
    {
        if (container.FormatKind != (byte)JsonTokenKind.StartObject)
            return Array.Empty<byte[]>();

        var keys = new List<byte[]>();
        long at = reader.FirstChildPosition(container.ValueStart);
        while (keys.Count < MaxKeys && reader.TryReadChild((byte)JsonTokenKind.StartObject, ref at, out var member, out _))
        {
            keys.Add(text.NameBytes(member).ToArray());

            at = text.End(member);
            if (at == long.MaxValue)
                break;
        }

        return keys;
    }
}
