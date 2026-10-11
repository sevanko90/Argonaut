using System;

namespace Argonaut.Ui.Find;

/// <summary>The find bar's status, twice over: <paramref name="Label"/> is the short form shown
/// in the search field, and <paramref name="Description"/> the full wording, its tooltip.</summary>
public readonly record struct FindStatus(string Label, string Description)
{
    /// <summary>Nothing found yet, scans still running.</summary>
    public static FindStatus Searching { get; } = new("Searching…", "Searching…");
}

/// <summary>
/// The find bar's status - a pure function of the search's state, so all five shapes and
/// their qualifiers can be read, and tested, without starting a scan. The full wording:
///   "Searching…"                  nothing found yet, scans still running
///   "No matches"                  scans finished, nothing found
///   "Search failed"               scans finished having never read their file at all
///   "3 of 47 rows"                a stop is selected
///   "47 rows"                     stops exist but none is selected (the selected one was
///                                 collapsed away by a fold, or none has been visited yet)
/// plus " (searching…)" while scans run, " (first n only)" at the match cap, and " — wrapped"
/// on the press that wrapped.
///
/// The label has to fit in the search field beside the term, so it is the count alone, large
/// totals rounded ("of 12.3k", "of 1M"), with "+" at the match cap and "…" while scans run:
///   "3 of 47", "7 of 1M+", "512 of 12.3k…", "47 matches", and the three bare states as they are.
/// </summary>
public static class FindStatusText
{
    public static FindStatus Compose(int stopCount, int position, string? stopUnit,
        bool scansComplete, bool hitCap, bool allFailedToOpen, bool wrapped)
    {
        if (stopCount == 0)
        {
            if (!scansComplete)
                return FindStatus.Searching;

            // "No matches" would be a lie when the file was never read - deleted, locked or
            // replaced between opening the document and searching it.
            string bare = allFailedToOpen ? "Search failed" : "No matches";
            return new FindStatus(bare, bare);
        }

        return new FindStatus(Label(stopCount, position, stopUnit, scansComplete, hitCap),
            Description(stopCount, position, stopUnit, scansComplete, hitCap, wrapped));
    }

    private static string Label(int stopCount, int position, string? stopUnit, bool scansComplete, bool hitCap)
    {
        string total = Rounded(stopCount) + (!scansComplete ? "…" : hitCap ? "+" : "");
        return position > 0
            ? $"{position:N0} of {total}"
            : $"{total} {(stopCount == 1 ? Singularize(stopUnit ?? "matches") : stopUnit ?? "matches")}";
    }

    /// <summary>A count as the label shows it: whole below ten thousand, then thousands or
    /// millions to three figures.</summary>
    private static string Rounded(int count) => count switch
    {
        < 10_000 => count.ToString("N0"),
        < 100_000 => $"{Math.Floor(count / 100.0) / 10:0.#}k",
        < 1_000_000 => $"{count / 1_000}k",
        < 10_000_000 => $"{Math.Floor(count / 100_000.0) / 10:0.#}M",
        _ => $"{count / 1_000_000}M",
    };

    private static string Description(int stopCount, int position, string? stopUnit, bool scansComplete, bool hitCap, bool wrapped)
    {
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
