namespace Argonaut.Features.Search;

/// <summary>
/// The find bar's status line - a pure function of the search's state, so all five shapes and
/// their qualifiers can be read, and tested, without starting a scan:
///   "Searching…"                  nothing found yet, scans still running
///   "No matches"                  scans finished, nothing found
///   "Search failed"               scans finished having never read their file at all
///   "3 of 47 rows"                a stop is selected
///   "47 rows"                     stops exist but none is selected (the selected one was
///                                 collapsed away by a fold, or none has been visited yet)
/// plus " (searching…)" while scans run, " (first n only)" at the match cap, and " — wrapped"
/// on the press that wrapped.
/// </summary>
public static class FindStatusText
{
    public static string Compose(int stopCount, int position, string? stopUnit,
        bool scansComplete, bool hitCap, bool allFailedToOpen, bool wrapped)
    {
        if (stopCount == 0)
        {
            if (!scansComplete)
                return "Searching…";

            // "No matches" would be a lie when the file was never read - deleted, locked or
            // replaced between opening the document and searching it.
            return allFailedToOpen ? "Search failed" : "No matches";
        }

        string text = position > 0
            ? $"{position:N0} of {stopCount:N0}{(stopUnit is null ? "" : " " + stopUnit)}"
            : $"{stopCount:N0} {(stopCount == 1 ? Singularize(stopUnit ?? "matches") : stopUnit ?? "matches")}";

        if (!scansComplete)
            text += " (searching…)";
        else if (hitCap)
            text += $" (first {stopCount:N0} only)";

        if (wrapped)
            text += " — wrapped";

        return text;
    }

    /// <summary>Singular of a unit, so a lone result is not "1 rows". The "-es" branch is not
    /// hypothetical: the default unit is "matches", which plain "-s" trimming rendered as
    /// "1 matche".</summary>
    private static string Singularize(string plural)
    {
        if (plural.Length > 2 && plural.EndsWith("es") && plural[^3] is 'h' or 's' or 'x' or 'z')
            return plural[..^2];

        return plural.EndsWith('s') ? plural[..^1] : plural;
    }
}
