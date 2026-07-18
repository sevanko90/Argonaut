using System;
using System.Collections.Generic;

namespace Argonaut.Features.Search;

/// <summary>
/// Splits a display string into match/non-match segments for the current find term
/// (ordinal, case-insensitive - mirroring LiteralSearchMatcher's ASCII folding closely
/// enough for display purposes). Pure so it's unit-testable without any UI.
/// </summary>
public static class SearchTextSplitter
{
    public readonly record struct Segment(int Start, int Length, bool IsMatch);

    /// <summary>
    /// Returns the segments covering all of <paramref name="text"/>, or null when no
    /// highlighting applies (empty term or no occurrence) so callers can take a plain-text
    /// fast path.
    /// </summary>
    public static IReadOnlyList<Segment>? Split(string text, string? term)
    {
        if (string.IsNullOrEmpty(term) || text.Length < term.Length)
            return null;

        List<Segment>? segments = null;
        int pos = 0;

        while (pos <= text.Length - term.Length)
        {
            int hit = text.IndexOf(term, pos, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
                break;

            segments ??= new List<Segment>();
            if (hit > pos)
                segments.Add(new Segment(pos, hit - pos, IsMatch: false));

            segments.Add(new Segment(hit, term.Length, IsMatch: true));
            pos = hit + term.Length;
        }

        if (segments is null)
            return null;

        if (pos < text.Length)
            segments.Add(new Segment(pos, text.Length - pos, IsMatch: false));

        return segments;
    }
}
