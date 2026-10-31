using System.Text;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Text;

namespace Argonaut.Shell;

/// <summary>How the status bar words a <see cref="LineEndingTally"/>.</summary>
internal static class LineEndingText
{
    /// <summary><c>LF</c>, <c>CRLF</c>, <c>CR</c> or <c>Mixed</c>; null when there were no line breaks.</summary>
    public static string? Label(LineEndingTally tally) => tally.Style switch
    {
        LineEndingStyle.Lf => "LF",
        LineEndingStyle.CrLf => "CRLF",
        LineEndingStyle.Cr => "CR",
        LineEndingStyle.Mixed => "Mixed",
        _ => null,
    };

    /// <summary>What was counted - the whole file, or only its start - and how many of each kind.</summary>
    public static string? Describe(LineEndingTally tally)
    {
        if (tally.Style == LineEndingStyle.None)
            return null;

        var text = new StringBuilder(tally.WholeInput
            ? "Line endings in the whole file"
            : $"Line endings in the first {ByteLengthText.Format(tally.SampledBytes)}");
        Append(text, "LF", tally.Lf);
        Append(text, "CRLF", tally.CrLf);
        Append(text, "CR", tally.LoneCr);
        return text.ToString();
    }

    private static void Append(StringBuilder text, string kind, long count)
    {
        if (count > 0)
            text.Append('\n').Append(kind).Append(": ").Append(count.ToString("N0"));
    }
}
