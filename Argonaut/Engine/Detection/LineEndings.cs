using System;

namespace Argonaut.Engine.Detection;

/// <summary>Which line breaks a document uses.</summary>
public enum LineEndingStyle
{
    /// <summary>No line break was seen.</summary>
    None,

    /// <summary>Every break is a lone <c>\n</c>.</summary>
    Lf,

    /// <summary>Every break is <c>\r\n</c>.</summary>
    CrLf,

    /// <summary>Every break is a lone <c>\r</c>.</summary>
    Cr,

    /// <summary>More than one kind of break was seen.</summary>
    Mixed,
}

/// <summary>
/// How many of each kind of line break a sample of a document holds. A file carries no marker for
/// its line endings, so they are counted from its bytes; <see cref="WholeInput"/> says whether the
/// sample was the whole document or only its start.
/// </summary>
/// <param name="Lf">Lone <c>\n</c> breaks.</param>
/// <param name="CrLf"><c>\r\n</c> breaks.</param>
/// <param name="LoneCr">Lone <c>\r</c> breaks.</param>
/// <param name="SampledBytes">How many bytes from the start were counted.</param>
/// <param name="WholeInput">The sample covered the whole document.</param>
public readonly record struct LineEndingTally(long Lf, long CrLf, long LoneCr, long SampledBytes, bool WholeInput)
{
    /// <summary>The kind of break the sample shows, <see cref="LineEndingStyle.Mixed"/> when more
    /// than one kind is present.</summary>
    public LineEndingStyle Style
    {
        get
        {
            int kinds = (Lf > 0 ? 1 : 0) + (CrLf > 0 ? 1 : 0) + (LoneCr > 0 ? 1 : 0);
            if (kinds == 0)
                return LineEndingStyle.None;
            if (kinds > 1)
                return LineEndingStyle.Mixed;
            return Lf > 0 ? LineEndingStyle.Lf : CrLf > 0 ? LineEndingStyle.CrLf : LineEndingStyle.Cr;
        }
    }

    /// <summary>
    /// The break a new line in this document should use, the way an editor follows a file's
    /// convention: the only kind present, or the most common in a mixed document. Null when there
    /// is nothing to follow - no breaks, or a tie - so the caller falls back to its own default.
    /// </summary>
    public LineEndingStyle? Prevailing
    {
        get
        {
            long most = Math.Max(Lf, Math.Max(CrLf, LoneCr));
            if (most == 0)
                return null;

            int leaders = (Lf == most ? 1 : 0) + (CrLf == most ? 1 : 0) + (LoneCr == most ? 1 : 0);
            if (leaders > 1)
                return null;

            return Lf == most ? LineEndingStyle.Lf : CrLf == most ? LineEndingStyle.CrLf : LineEndingStyle.Cr;
        }
    }
}

/// <summary>The bytes of each kind of line break.</summary>
public static class LineEndingBytes
{
    private static readonly byte[] LfBytes = [(byte)'\n'];
    private static readonly byte[] CrLfBytes = [(byte)'\r', (byte)'\n'];
    private static readonly byte[] CrBytes = [(byte)'\r'];

    /// <summary>The bytes <paramref name="style"/> writes; only a single kind of break has them.</summary>
    public static ReadOnlySpan<byte> Of(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Lf => LfBytes,
        LineEndingStyle.CrLf => CrLfBytes,
        LineEndingStyle.Cr => CrBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Only LF, CRLF and CR are line breaks."),
    };

    /// <summary>The platform's own convention: CRLF on Windows, LF elsewhere.</summary>
    public static LineEndingStyle OperatingSystemDefault
        => OperatingSystem.IsWindows() ? LineEndingStyle.CrLf : LineEndingStyle.Lf;
}
