using System;
using System.Collections.Generic;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Decides whether one schema in the catalog fits a newly opened document well enough to bind it
/// with nobody having chosen it - "Open matched schemas".
///
/// A stricter bar than the picker's. <see cref="JsonSchemaRootMatcher.IsPlausible"/> decides
/// what to list first when the user is choosing anyway; this decides what to bind when they are
/// not, so a wrong answer is a label nobody asked for. Declining is always available and always
/// safe: the document opens as it did before this existed.
///
/// <c>required</c> is read here, and only here, as evidence - never to validate a document. A
/// type whose required names are not all present is not bound; a small type whose required names
/// are all present can be. A schema the user picks by hand is bound regardless.
///
/// Pure and allocation-light, like the matcher it builds on.
/// </summary>
public static class SchemaAutoMatchRule
{
    /// <summary>The broad path: enough shared names that a match is not a coincidence...</summary>
    public const int MinimumBroadMatchedKeys = 6;

    /// <summary>...and the type explains most of the document...</summary>
    public const double MinimumBroadCoverage = 0.7;

    /// <summary>...or the document carries nearly everything the type declares - a partial,
    /// hand-written schema for a large object.</summary>
    public const double MinimumBroadPrecision = 0.95;

    /// <summary>
    /// The small path: a type with too few properties to clear the broad bar - GeoJSON's
    /// FeatureCollection declares three - is bound when it requires at least this many names, all
    /// present, and the document carries nothing it does not declare. One required name is not
    /// enough: <c>type</c> alone is required by half the schemas ever written.
    /// </summary>
    public const int MinimumRequiredForSmallMatch = 2;

    /// <summary>
    /// The schema and type to bind for a document whose sampled property names are
    /// <paramref name="documentKeys"/>, or null when nothing fits clearly enough: no type clears
    /// the bar, or another schema fits about as well, or the best schema's own types tie.
    /// </summary>
    public static (int SchemaIndex, SchemaRootMatch Match)? Choose(IReadOnlyList<SchemaFingerprint> schemas, IReadOnlyList<byte[]> documentKeys)
        => Assess(schemas, documentKeys).Chosen;

    /// <summary>
    /// <see cref="Choose"/>'s answer together with what it was chosen from: each schema's leading
    /// type and score (null for a schema with no type to score), in <paramref name="schemas"/>'
    /// order - which is what ranking the schema list by fit reads.
    /// </summary>
    public static (SchemaRootMatch?[] Leaders, (int SchemaIndex, SchemaRootMatch Match)? Chosen) Assess(IReadOnlyList<SchemaFingerprint> schemas, IReadOnlyList<byte[]> documentKeys)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(documentKeys);

        // Each schema's leader, plausible or not: a schema whose own types tie cannot win, but
        // its leader still competes - it is still a schema that fits.
        var leaders = new SchemaRootMatch?[schemas.Count];
        if (documentKeys.Count == 0)
            return (leaders, null);

        int winner = -1;
        for (int i = 0; i < schemas.Count; i++)
        {
            var ranked = JsonSchemaRootMatcher.Rank(schemas[i], documentKeys);
            if (ranked.Count == 0)
                continue;

            leaders[i] = ranked[0];

            if (JsonSchemaRootMatcher.Best(ranked) is { } best && Qualifies(best) &&
                (winner < 0 || Before(best, leaders[winner]!.Value)))
                winner = i;
        }

        if (winner < 0)
            return (leaders, null);

        var chosen = leaders[winner]!.Value;
        for (int i = 0; i < schemas.Count; i++)
        {
            if (i != winner && leaders[i] is { } other && JsonSchemaRootMatcher.IsPlausible(other) &&
                Math.Abs(chosen.Coverage - other.Coverage) < JsonSchemaRootMatcher.AmbiguityMargin &&
                Math.Abs(chosen.Precision - other.Precision) < JsonSchemaRootMatcher.AmbiguityMargin)
                return (leaders, null);
        }

        return (leaders, (winner, chosen));
    }

    /// <summary>Whether one type, already the clear leader of its own schema, is good enough to
    /// bind unasked.</summary>
    public static bool Qualifies(SchemaRootMatch match)
    {
        if (match.RequiredPresent < match.RequiredKeys)
            return false;

        bool broad = match.MatchedKeys >= MinimumBroadMatchedKeys
            && (match.Coverage >= MinimumBroadCoverage || match.Precision >= MinimumBroadPrecision);

        bool smallAndComplete = match.RequiredKeys >= MinimumRequiredForSmallMatch
            && match.MatchedKeys == match.DocumentKeys;

        return broad || smallAndComplete;
    }

    /// <summary>The order <see cref="JsonSchemaRootMatcher.Rank(SchemaFingerprint, IReadOnlyList{byte[]})"/>
    /// uses: coverage, then precision.</summary>
    internal static bool Before(SchemaRootMatch a, SchemaRootMatch b)
        => a.Coverage != b.Coverage ? a.Coverage > b.Coverage : a.Precision > b.Precision;
}
