using System;

namespace Argonaut.Engine.Text;

/// <summary>
/// The characters a single-line display swaps for a visible glyph, one char for one: C0 controls
/// and DEL become their Unicode Control Pictures, and the three line separators that are not
/// '\n' become a symbol of their own. Every view that draws a row of decoded text into one
/// fixed-height line goes through this - the raw view's rows, the trees' rows, the grids' cells,
/// the NDJSON line list - so they agree about what such a character looks like.
///
/// It is for drawing only. Text that leaves the app (a copied value, a JSONPath) keeps the real
/// characters, which is why this is applied where text is laid out rather than where it is
/// decoded.
/// </summary>
public static class ControlGlyphs
{
    private const char Delete = (char)0x7F;

    /// <summary>Unicode Control Picture for DEL (U+2421).</summary>
    private const char DeletePicture = (char)0x2421;

    /// <summary>Start of the Control Pictures block (U+2400), which runs parallel to C0.</summary>
    private const int ControlPicturesBase = 0x2400;

    /// <summary>U+0085 NEXT LINE, the C1 control - Unicode's form of EBCDIC's NL.</summary>
    private const char NextLine = (char)0x0085;

    /// <summary>U+2028 LINE SEPARATOR.</summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>U+2029 PARAGRAPH SEPARATOR.</summary>
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>U+2424 SYMBOL FOR NEWLINE, shown for <see cref="NextLine"/>.</summary>
    public const char NextLinePicture = (char)0x2424;

    /// <summary>U+21B5, shown for <see cref="LineSeparator"/>.</summary>
    public const char LineSeparatorPicture = (char)0x21B5;

    /// <summary>U+00B6 PILCROW, shown for <see cref="ParagraphSeparator"/>.</summary>
    public const char ParagraphSeparatorPicture = (char)0x00B6;

    /// <summary>
    /// C0 controls, DEL, and the three separators that are not '\n'; tab passes through (the text
    /// layout renders it).
    ///
    /// NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR are mandatory line breaks to Unicode (UAX #14)
    /// and so to the text layout, which would otherwise stack several lines of text inside one
    /// 22px row band and clip all but the first. They are not breaks to this app: the raw view
    /// breaks rows on '\n' bytes, because that is what every tool a user cross-references a line
    /// number with counts, and JSON allows all three unescaped inside a string, so a tree row's
    /// value can hold one. A lone CR is handled the same way - shown inline as a glyph.
    /// </summary>
    public static bool IsDisplayControl(char c)
        => (c < ' ' && c != '\t')
            || c == Delete
            || c == NextLine
            || c == LineSeparator
            || c == ParagraphSeparator;

    /// <summary>The glyph drawn for <paramref name="c"/>, or <paramref name="c"/> itself.</summary>
    public static char ForDisplay(char c)
        => !IsDisplayControl(c) ? c
            : c == Delete ? DeletePicture
            : c == NextLine ? NextLinePicture
            : c == LineSeparator ? LineSeparatorPicture
            : c == ParagraphSeparator ? ParagraphSeparatorPicture
            : (char)(ControlPicturesBase + c);

    /// <summary>
    /// <paramref name="text"/> with every display control swapped for its glyph. The same length,
    /// so any char offset into the original - a run, a link, a search match - still lines up.
    /// Clean text, the common case, is returned as-is with no allocation.
    /// </summary>
    public static string ForDisplay(string text)
    {
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsDisplayControl(text[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
            return text;

        return string.Create(text.Length, (text, first), static (dest, state) =>
        {
            var (source, from) = state;
            source.AsSpan(0, from).CopyTo(dest);
            for (int i = from; i < source.Length; i++)
                dest[i] = ForDisplay(source[i]);
        });
    }

    /// <summary>
    /// True for the glyphs <see cref="ForDisplay(char)"/> produces. A real U+2400-U+2426, U+21B5
    /// or U+00B6 in the text is the same char and answers the same way.
    /// </summary>
    public static bool IsSubstitutionGlyph(char c)
        => c is >= (char)ControlPicturesBase and <= (char)0x2426
            || c == LineSeparatorPicture
            || c == ParagraphSeparatorPicture;
}
