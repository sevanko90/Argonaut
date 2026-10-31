using System.Text;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Text;

namespace Argonaut.Shell;

/// <summary>How the status bar words a <see cref="LineEndingTally"/>.</summary>
internal static class LineEndingText
{
    /// <summary>
    /// <c>LF</c>, <c>CRLF</c>, <c>CR</c> or <c>Mixed</c>, then <c>→ CRLF</c> when the next save
    /// converts them; null when there is nothing to say - no count, or no line breaks and nothing
    /// pending.
    /// </summary>
    /// <param name="newLines">For a document with no breaks yet, the break its new lines use -
    /// shown in place of a count there is nothing in.</param>
    public static string? Label(LineEndingTally? tally, LineEndingStyle? pending = null, LineEndingStyle? newLines = null)
    {
        if (tally is { Style: LineEndingStyle.None } && newLines is { } future)
            return Name(future);

        string? counted = tally is { } t ? Name(t.Style) : null;
        if (pending is not { } target)
            return counted;

        return counted is null ? $"→ {Name(target)}" : $"{counted} → {Name(target)}";
    }

    /// <summary>What was counted - the whole file, or only its start - how many of each kind, and
    /// the conversion the next save makes.</summary>
    public static string? Describe(LineEndingTally? tally, LineEndingStyle? pending = null, LineEndingStyle? newLines = null)
    {
        if (tally is { Style: LineEndingStyle.None } && newLines is { } future)
            return $"No line breaks yet\nNew lines use {Name(future)}";

        var text = new StringBuilder();
        if (tally is { Style: not LineEndingStyle.None } t)
        {
            text.Append(t.WholeInput
                ? "Line endings in the whole file"
                : $"Line endings in the first {ByteLengthText.Format(t.SampledBytes)}");
            Append(text, "LF", t.Lf);
            Append(text, "CRLF", t.CrLf);
            Append(text, "CR", t.LoneCr);
        }

        if (pending is { } target)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append("Converted to ").Append(Name(target)).Append(" when saved");
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private static string? Name(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Lf => "LF",
        LineEndingStyle.CrLf => "CRLF",
        LineEndingStyle.Cr => "CR",
        LineEndingStyle.Mixed => "Mixed",
        _ => null,
    };

    private static void Append(StringBuilder text, string kind, long count)
    {
        if (count > 0)
            text.Append('\n').Append(kind).Append(": ").Append(count.ToString("N0"));
    }
}
