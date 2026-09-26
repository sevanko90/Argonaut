using System;

namespace Argonaut.Features.Json.Schema;

/// <summary>What a schema gutter shows for a node: its title, or failing that the opening line of
/// its description.</summary>
public static class JsonSchemaLabel
{
    /// <summary>
    /// The first line of a schema description, for use as a gutter label. Descriptions are prose
    /// and often several paragraphs (frequently with a trailing "Schema link: …"), so only the
    /// opening line is a candidate; the full text stays on the tooltip.
    ///
    /// The hard cap is a measuring guard, not the visible limit - the gutter cell trims to the
    /// gutter's current width and shows an ellipsis there - so nothing is cut short enough for the
    /// cap to be what the user sees. Returns the original string when neither applies, so the
    /// common short description costs no allocation.
    /// </summary>
    private const int MaxSchemaLabelLength = 200;

    public static string? FirstLine(string? description)
    {
        if (description is null)
            return null;

        int end = description.IndexOfAny(NewLineChars);

        // Descriptions written for a docs site break paragraphs with a literal line-break tag as
        // often as with a newline, and everything after the first break is no more wanted on the
        // row than a second paragraph would be. Both spellings occur - the real OpenAPI document
        // this was built against uses the (invalid, but common) closing form.
        int tag = EarliestOf(description, "<br", "</br");
        if (tag >= 0 && (end < 0 || tag < end))
            end = tag;

        if (end < 0)
            end = description.Length;
        if (end > MaxSchemaLabelLength)
            end = MaxSchemaLabelLength;

        return end == description.Length ? description : description[..end].TrimEnd();
    }

    private static readonly char[] NewLineChars = { '\r', '\n' };

    private static int EarliestOf(string text, string a, string b)
    {
        int first = text.IndexOf(a, StringComparison.OrdinalIgnoreCase);
        int second = text.IndexOf(b, StringComparison.OrdinalIgnoreCase);

        if (first < 0)
            return second;

        return second < 0 ? first : Math.Min(first, second);
    }
}
