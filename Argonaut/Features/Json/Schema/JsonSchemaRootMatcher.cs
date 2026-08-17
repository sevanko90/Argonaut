using System;
using System.Collections.Generic;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// How well one of a schema's named roots fits the open document, as counts and the two ratios
/// derived from them. Both ratios matter and neither alone is enough: an "envelope" type
/// declaring every key in the document scores perfectly on <see cref="Coverage"/> while
/// explaining almost nothing, and a two-property type scores perfectly on
/// <see cref="Precision"/> for any document that happens to carry those two keys.
/// </summary>
/// <param name="Name">The named root this scored, or null for the schema's own root - which is a
/// candidate like any other whenever it is usable. A schema that discriminates internally (one
/// root object whose <c>properties</c> describe the whole file, with <c>$defs</c> holding only its
/// inner pieces) is matched by its root and by nothing else, so excluding it would report "no type
/// recognised" for exactly the schemas that need no type chosen at all.</param>
/// <param name="Coverage">Fraction of the document's own keys the schema declares - "how much of
/// what I'm looking at does this type explain".</param>
/// <param name="Precision">Fraction of the schema's properties the document actually carries -
/// "how much of this type is really here", which is what separates a tight match from a superset.</param>
public readonly record struct SchemaRootMatch(
    string? Name,
    int NodeId,
    int MatchedKeys,
    int DocumentKeys,
    int SchemaKeys,
    double Coverage,
    double Precision);

/// <summary>
/// Ranks a schema's <see cref="JsonSchemaDocument.NamedRoots"/> against the property names the
/// open document actually carries, so a file holding hundreds of schemas (an OpenAPI document
/// runs to hundreds) can be presented best-fit-first instead of alphabetically. Without this the
/// user has to already know which type their document is, which is the one thing a list of type
/// names cannot tell them.
///
/// Deliberately name-only: no <c>type</c>, <c>format</c>, or value inspection. Reading values to
/// discriminate would mean touching document content during what is otherwise a pure structural
/// comparison, which is exactly the cost the schema-hints design exists to avoid. Property names
/// alone turn out to separate real API types well, because that is what makes them different
/// types in the first place.
///
/// Pure and allocation-light - no I/O, no index access, no mapping reads - so it is trivially
/// testable and safe to call from either thread.
/// </summary>
public static class JsonSchemaRootMatcher
{
    /// <summary>A candidate has to explain at least this much of the document to be offered as a
    /// match rather than merely listed. Below it, the honest answer is "no idea" - unless the
    /// candidate qualifies on precision instead, see <see cref="MinimumPrecision"/>.</summary>
    public const double MinimumCoverage = 0.5;

    /// <summary>
    /// One shared key is not evidence - `id` or `data` alone matches dozens of unrelated types.
    /// A single-key document therefore never produces a match, which is correct: there is nothing
    /// to go on.
    /// </summary>
    public const int MinimumMatchedKeys = 2;

    /// <summary>
    /// The second way to qualify: near enough every property the *candidate* declares is present
    /// in the document, even though the document carries far more besides.
    ///
    /// This is what a partial schema looks like. The bundled Keepa schema declares 13 properties
    /// for a product; a real Keepa product object carries about a hundred. Scored on coverage
    /// alone that type explains 13% of the document and is rejected, so the one schema that
    /// obviously fits gets dumped into the alphabetical list and has to be found by hand. Scored
    /// on precision it is 13 of 13 - every field the schema knows about is there, and nothing it
    /// claims is missing.
    ///
    /// Not 1.0, because a schema written against one API version and pointed at the next is still
    /// the right answer with a field or two retired.
    /// </summary>
    public const double MinimumPrecision = 0.9;

    /// <summary>
    /// Guards the precision path against the failure precision has on its own: a three-property
    /// type scores 100% on any document that happens to carry those three names. Enough declared
    /// properties all landing is evidence; a handful is coincidence.
    /// </summary>
    public const int MinimumPreciseMatchedKeys = 4;

    /// <summary>Two candidates this close are not distinguishable on names alone, so neither is
    /// promoted - the user has to choose between (say) a Commit and a Retrieve response that
    /// share an envelope.</summary>
    public const double AmbiguityMargin = 0.05;

    /// <summary>
    /// Every named root scored against <paramref name="documentKeys"/>, best first: highest
    /// coverage, then highest precision, then name. Includes candidates that scored nothing, so
    /// the caller has one ordered list to present rather than having to merge two.
    ///
    /// <paramref name="documentKeys"/> is the raw UTF-8 of the property names on the document
    /// node being matched, in any order and possibly with duplicates.
    /// </summary>
    public static IReadOnlyList<SchemaRootMatch> Rank(JsonSchemaDocument schema, IReadOnlyList<byte[]> documentKeys)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(documentKeys);

        var roots = schema.NamedRoots;
        if (roots.Count == 0)
            return Array.Empty<SchemaRootMatch>();

        // Sorted (and de-duplicated) so each candidate is a linear merge against the schema's
        // already-sorted key array rather than a nested scan.
        var keys = SortedDistinct(documentKeys);

        var matches = new List<SchemaRootMatch>(roots.Count + 1);

        // The schema's own root competes with the named ones whenever it is usable - see the
        // remark on SchemaRootMatch.Name.
        if (schema.DocumentRootIsUsable)
            matches.Add(Score(schema, name: null, schema.DocumentRootId, keys));

        foreach (var root in roots)
            matches.Add(Score(schema, root.Name, root.NodeId, keys));

        matches.Sort(static (a, b) =>
        {
            // Qualifying candidates lead, whichever way they qualified. Coverage alone can't order
            // them any more: a precision-qualified subset scores *below* junk that shares more
            // names with the document, and callers (the picker's shortlist loop, Best) read the
            // head of this list expecting the plausible ones to be contiguous.
            int byPlausible = IsPlausible(b).CompareTo(IsPlausible(a));
            if (byPlausible != 0)
                return byPlausible;

            int byCoverage = b.Coverage.CompareTo(a.Coverage);
            if (byCoverage != 0)
                return byCoverage;

            int byPrecision = b.Precision.CompareTo(a.Precision);
            if (byPrecision != 0)
                return byPrecision;

            // The schema's own root wins an outright tie: a schema whose root and whose $defs
            // describe the same shape is one the user shouldn't have to pick a type for.
            if (a.Name is null || b.Name is null)
                return a.Name is null ? (b.Name is null ? 0 : -1) : 1;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return matches;
    }

    private static SchemaRootMatch Score(JsonSchemaDocument schema, string? name, int nodeId, List<byte[]> documentKeys)
    {
        var schemaKeys = schema.GetPropertyKeys(nodeId);
        int matched = CountCommon(documentKeys, schemaKeys);

        return new SchemaRootMatch(
            name,
            nodeId,
            matched,
            documentKeys.Count,
            schemaKeys.Count,
            documentKeys.Count == 0 ? 0 : (double)matched / documentKeys.Count,
            schemaKeys.Count == 0 ? 0 : (double)matched / schemaKeys.Count);
    }

    /// <summary>
    /// The single best candidate from <see cref="Rank"/>, or null when there isn't one worth
    /// offering: nothing cleared the thresholds, or the top two are too close to separate.
    ///
    /// Declining is a real answer, not a failure. A confidently-wrong type label is worse than no
    /// label, because the user has no reason to distrust it.
    /// </summary>
    public static SchemaRootMatch? Best(IReadOnlyList<SchemaRootMatch> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        if (ranked.Count == 0)
            return null;

        var best = ranked[0];
        if (!IsPlausible(best))
            return null;

        if (ranked.Count > 1)
        {
            var runnerUp = ranked[1];
            if (Math.Abs(best.Coverage - runnerUp.Coverage) < AmbiguityMargin &&
                Math.Abs(best.Precision - runnerUp.Precision) < AmbiguityMargin)
                return null;
        }

        return best;
    }

    /// <summary>Whether a scored candidate is good enough to sit in the "best match" section,
    /// which is a lower bar than <see cref="Best"/>: several plausible types listed together is
    /// an honest answer to an ambiguous document, where auto-selecting one would not be.
    ///
    /// Either measure can qualify a candidate, because they answer different questions and a real
    /// schema can be strong on one and weak on the other. A complete schema explains the document
    /// (coverage); a partial one is fully accounted for by it (precision). Requiring both would
    /// reject every schema that documents less than half of what an API actually returns, which is
    /// most hand-written ones.</summary>
    public static bool IsPlausible(SchemaRootMatch match)
        => (match.MatchedKeys >= MinimumMatchedKeys && match.Coverage >= MinimumCoverage)
        || (match.MatchedKeys >= MinimumPreciseMatchedKeys && match.Precision >= MinimumPrecision);

    /// <summary>Whether a candidate qualified only as a subset of the document - every field the
    /// schema declares is present, but the document carries much more besides. The picker says so
    /// rather than badging such a match with its (honest, but alarming) coverage percentage.</summary>
    public static bool IsSubsetMatch(SchemaRootMatch match)
        => IsPlausible(match) && match.Coverage < MinimumCoverage;

    private static List<byte[]> SortedDistinct(IReadOnlyList<byte[]> keys)
    {
        var sorted = new List<byte[]>(keys.Count);
        foreach (var key in keys)
        {
            if (key is not null)
                sorted.Add(key);
        }

        sorted.Sort(static (a, b) => ((ReadOnlySpan<byte>)a).SequenceCompareTo(b));

        for (int i = sorted.Count - 1; i > 0; i--)
        {
            if (((ReadOnlySpan<byte>)sorted[i]).SequenceEqual(sorted[i - 1]))
                sorted.RemoveAt(i);
        }

        return sorted;
    }

    /// <summary>Linear merge of two ordinally-sorted UTF-8 key lists.</summary>
    private static int CountCommon(List<byte[]> documentKeys, IReadOnlyList<byte[]> schemaKeys)
    {
        int common = 0;
        int i = 0, j = 0;

        while (i < documentKeys.Count && j < schemaKeys.Count)
        {
            int comparison = ((ReadOnlySpan<byte>)documentKeys[i]).SequenceCompareTo(schemaKeys[j]);
            if (comparison == 0)
            {
                common++;
                i++;
                j++;
            }
            else if (comparison < 0)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return common;
    }
}
