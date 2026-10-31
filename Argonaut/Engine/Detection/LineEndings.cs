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
}
