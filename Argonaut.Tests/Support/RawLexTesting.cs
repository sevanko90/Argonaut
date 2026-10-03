using Argonaut.Features.Raw.Highlighting;

namespace Argonaut.Tests.Support;

/// <summary>Helpers for asserting on what a raw lexer made of a row, one char at a time.</summary>
internal static class RawLexTesting
{
    /// <summary>The style of every char of a row; chars no span covers are plain.</summary>
    public static RawTextStyle[] StylesOf(IEnumerable<RawStyledSpan> spans, int length)
    {
        var styles = new RawTextStyle[length];
        int previousEnd = 0;
        foreach (var span in spans)
        {
            Assert.True(span.Start >= previousEnd, "spans must ascend without overlapping");
            Assert.True(span.Length > 0 && span.Start + span.Length <= length, "span must lie inside the row");
            previousEnd = span.Start + span.Length;
            for (int i = span.Start; i < previousEnd; i++)
                styles[i] = span.Style;
        }

        return styles;
    }

    public static (List<RawStyledSpan> Spans, RawLexState Exit) Lex(IRawLexer lexer, string row, RawLexState entry)
    {
        var spans = new List<RawStyledSpan>();
        var exit = lexer.Lex(row, entry, spans);
        return (spans, exit);
    }

    /// <summary>Per-char styles of a whole line lexed in one go.</summary>
    public static RawTextStyle[] WholeLine(IRawLexer lexer, string line)
        => StylesOf(Lex(lexer, line, RawLexState.LineStart).Spans, line.Length);

    /// <summary>Per-char styles of a line cut at <paramref name="k"/> and lexed as two rows.</summary>
    public static RawTextStyle[] SplitLine(IRawLexer lexer, string line, int k)
    {
        var (first, state) = Lex(lexer, line[..k], RawLexState.LineStart);
        var (second, _) = Lex(lexer, line[k..], state);
        var styles = new RawTextStyle[line.Length];
        StylesOf(first, k).CopyTo(styles, 0);
        StylesOf(second, line.Length - k).CopyTo(styles, k);
        return styles;
    }

    /// <summary>
    /// Asserts the split property for every cut of the line: the two halves colour the same as the
    /// whole, except that a key whose separator lies past the cut may be <paramref name="keyFallback"/>
    /// in the first half. Cuts for which <paramref name="skipCut"/> returns true are not checked.
    /// </summary>
    public static void AssertSplitsAgree(
        IRawLexer lexer, string line, RawTextStyle keyFallback = RawTextStyle.String, Func<int, bool>? skipCut = null)
    {
        var whole = WholeLine(lexer, line);
        for (int k = 0; k <= line.Length; k++)
        {
            if (skipCut?.Invoke(k) == true)
                continue;

            var split = SplitLine(lexer, line, k);
            for (int i = 0; i < line.Length; i++)
            {
                if (whole[i] == split[i])
                    continue;

                bool keyCutFromItsColon = whole[i] == RawTextStyle.Key && split[i] == keyFallback && i < k;
                Assert.True(keyCutFromItsColon, $"'{line}' cut at {k}: char {i} is {split[i]}, whole line says {whole[i]}");
            }
        }
    }
}
