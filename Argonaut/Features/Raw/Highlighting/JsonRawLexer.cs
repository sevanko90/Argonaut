using System;
using System.Collections.Generic;

namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// Colours JSON, NDJSON and JSONC rows. Line-local: a <c>/* */</c> that does not close on its
/// own line colours the rest of that line and no further.
///
/// A token cut by a row boundary is coloured on its own side of the cut as it would be whole, so
/// the carried state records the token in progress: an open string, a comment, a number, or a
/// bare word that so far reads as a prefix of <c>true</c>, <c>false</c> or <c>null</c>. The one
/// colour that depends on text after the cut is a key, whose <c>:</c> may fall on the next row;
/// the part before the cut is then a string.
/// </summary>
public sealed class JsonRawLexer : IRawLexer
{
    // The mode lives in the low four bits; the rest qualify it.
    private const int ModeMask = 0xF;
    private const int InString = 1;
    private const int InLineComment = 2;
    private const int InBlockComment = 3;
    private const int InNumber = 4;
    private const int InWord = 5;
    private const int AfterSlash = 6;
    private const int InKeyword = 7;

    private const int EscapePending = 1 << 4;
    private const int StarPending = 1 << 5;
    private const int KeywordShift = 6;     // 2 bits: which keyword
    private const int MatchedShift = 8;     // 4 bits: how many of its chars have been read

    private static readonly string[] Keywords = { "true", "false", "null" };

    private static readonly RawTextStyle[] KeywordStyles =
        { RawTextStyle.Keyword, RawTextStyle.Keyword, RawTextStyle.Literal };

    public string DisplayName => "JSON";

    public RawLexState Lex(ReadOnlySpan<char> row, RawLexState entry, List<RawStyledSpan> spans)
    {
        int n = row.Length;
        int i = 0;
        int bits = entry.Bits;

        switch (bits & ModeMask)
        {
            case InString:
            {
                int close = ScanString(row, 0, (bits & EscapePending) != 0, out bool escape);
                if (close < 0)
                {
                    if (n > 0)
                        spans.Add(new RawStyledSpan(0, n, RawTextStyle.String));
                    return new RawLexState(InString | (escape ? EscapePending : 0));
                }

                AddString(row, 0, close, spans);
                i = close;
                break;
            }

            case InLineComment:
                if (n > 0)
                    spans.Add(new RawStyledSpan(0, n, RawTextStyle.Comment));
                return entry;

            case InBlockComment:
                if (!ScanBlockComment(row, 0, 0, (bits & StarPending) != 0, spans, out i, out var stillOpen))
                    return stillOpen;
                break;

            case InNumber:
                i = ConsumeNumber(row, 0);
                if (i > 0)
                    spans.Add(new RawStyledSpan(0, i, RawTextStyle.Number));
                if (i == n && n > 0)
                    return new RawLexState(InNumber);
                if (n == 0)
                    return entry;
                break;

            case InWord:
                i = ConsumeWord(row, 0);
                if (i == n)
                    return n == 0 ? entry : new RawLexState(InWord);
                break;

            case AfterSlash:
                if (n > 0 && row[0] == '/')
                {
                    spans.Add(new RawStyledSpan(0, n, RawTextStyle.Comment));
                    return new RawLexState(InLineComment);
                }

                if (n > 0 && row[0] == '*')
                {
                    if (!ScanBlockComment(row, 0, 1, false, spans, out i, out var open))
                        return open;
                }

                if (n == 0)
                    return entry;
                break;

            case InKeyword:
            {
                int keyword = (bits >> KeywordShift) & 3;
                int matched = (bits >> MatchedShift) & 0xF;
                int end = ConsumeWord(row, 0);
                if (end == 0)
                {
                    if (n == 0)
                        return entry;
                    break;
                }

                var remainder = Keywords[keyword].AsSpan(matched);
                var run = row[..end];
                if (remainder.StartsWith(run))
                {
                    spans.Add(new RawStyledSpan(0, end, KeywordStyles[keyword]));
                    if (end == n)
                        return run.Length == remainder.Length
                            ? new RawLexState(InWord)
                            : KeywordProgress(keyword, matched + end);
                }
                else if (end == n)
                {
                    return new RawLexState(InWord);
                }

                i = end;
                break;
            }
        }

        while (i < n)
        {
            char c = row[i];

            if (RawLexChars.IsBlank(c))
            {
                i++;
            }
            else if (c == '"')
            {
                int close = ScanString(row, i + 1, false, out bool escape);
                if (close < 0)
                {
                    spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.String));
                    return new RawLexState(InString | (escape ? EscapePending : 0));
                }

                AddString(row, i, close, spans);
                i = close;
            }
            else if (c is '{' or '}' or '[' or ']' or ',' or ':')
            {
                spans.Add(new RawStyledSpan(i, 1, RawTextStyle.Punctuation));
                i++;
            }
            else if (c == '/')
            {
                if (i + 1 == n)
                {
                    // Optimistic: a '/' that ends the row is the start of a comment unless the
                    // next row says otherwise, and valid JSONC has no other use for one.
                    spans.Add(new RawStyledSpan(i, 1, RawTextStyle.Comment));
                    return new RawLexState(AfterSlash);
                }

                if (row[i + 1] == '/')
                {
                    spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Comment));
                    return new RawLexState(InLineComment);
                }

                if (row[i + 1] == '*')
                {
                    if (!ScanBlockComment(row, i, i + 2, false, spans, out i, out var open))
                        return open;
                }
                else
                {
                    i++;
                }
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                int end = ConsumeNumber(row, i);
                spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.Number));
                i = end;
                if (i == n)
                    return new RawLexState(InNumber);
            }
            else if (RawLexChars.IsWordChar(c))
            {
                int end = ConsumeWord(row, i);
                var run = row.Slice(i, end - i);
                int keyword = KeywordIndex(run, exact: true);

                if (end < n)
                {
                    if (keyword >= 0)
                        spans.Add(new RawStyledSpan(i, end - i, KeywordStyles[keyword]));
                    i = end;
                    continue;
                }

                // The word reaches the row's end, so it may continue on the next row.
                if (keyword >= 0)
                {
                    spans.Add(new RawStyledSpan(i, end - i, KeywordStyles[keyword]));
                    return new RawLexState(InWord);
                }

                int partial = KeywordIndex(run, exact: false);
                if (partial >= 0)
                {
                    spans.Add(new RawStyledSpan(i, end - i, KeywordStyles[partial]));
                    return KeywordProgress(partial, run.Length);
                }

                return new RawLexState(InWord);
            }
            else
            {
                i++;
            }
        }

        return RawLexState.LineStart;
    }

    private static RawLexState KeywordProgress(int keyword, int matched)
        => new(InKeyword | (keyword << KeywordShift) | (matched << MatchedShift));

    /// <summary>
    /// The keyword <paramref name="run"/> equals (<paramref name="exact"/>), or is a proper
    /// prefix of; -1 for none.
    /// </summary>
    private static int KeywordIndex(ReadOnlySpan<char> run, bool exact)
    {
        for (int k = 0; k < Keywords.Length; k++)
        {
            var word = Keywords[k].AsSpan();
            if (exact ? run.SequenceEqual(word) : run.Length < word.Length && word.StartsWith(run))
                return k;
        }

        return -1;
    }

    private static int ConsumeWord(ReadOnlySpan<char> row, int from)
    {
        int i = from;
        while (i < row.Length && RawLexChars.IsWordChar(row[i]))
            i++;
        return i;
    }

    private static int ConsumeNumber(ReadOnlySpan<char> row, int from)
    {
        int i = from;
        while (i < row.Length && (char.IsAsciiDigit(row[i]) || row[i] is '.' or 'e' or 'E' or '+' or '-'))
            i++;
        return i;
    }

    /// <summary>
    /// The index just past the closing quote at or after <paramref name="from"/>, or -1 when the
    /// string is still open at the row's end (<paramref name="escape"/> then says the row ended
    /// on a backslash).
    /// </summary>
    private static int ScanString(ReadOnlySpan<char> row, int from, bool escapePending, out bool escape)
    {
        escape = escapePending;
        for (int i = from; i < row.Length; i++)
        {
            if (escape)
                escape = false;
            else if (row[i] == '\\')
                escape = true;
            else if (row[i] == '"')
                return i + 1;
        }

        return -1;
    }

    /// <summary>A string is a key when a colon follows it, past any whitespace, on this row.</summary>
    private static void AddString(ReadOnlySpan<char> row, int start, int end, List<RawStyledSpan> spans)
    {
        int k = end;
        while (k < row.Length && RawLexChars.IsBlank(row[k]))
            k++;

        bool isKey = k < row.Length && row[k] == ':';
        spans.Add(new RawStyledSpan(start, end - start, isKey ? RawTextStyle.Key : RawTextStyle.String));
    }

    /// <summary>
    /// Colours a block comment that starts at <paramref name="start"/> and looks for its close
    /// from <paramref name="scanFrom"/>. Returns true with <paramref name="next"/> just past the
    /// close; false with the state to carry when the row ends first.
    /// </summary>
    private static bool ScanBlockComment(
        ReadOnlySpan<char> row, int start, int scanFrom, bool starPending,
        List<RawStyledSpan> spans, out int next, out RawLexState open)
    {
        int n = row.Length;
        next = n;
        open = default;

        if (starPending && n > 0 && row[0] == '/')
        {
            spans.Add(new RawStyledSpan(start, 1, RawTextStyle.Comment));
            next = 1;
            return true;
        }

        for (int j = scanFrom; j + 1 < n; j++)
        {
            if (row[j] == '*' && row[j + 1] == '/')
            {
                spans.Add(new RawStyledSpan(start, j + 2 - start, RawTextStyle.Comment));
                next = j + 2;
                return true;
            }
        }

        if (n > start)
            spans.Add(new RawStyledSpan(start, n - start, RawTextStyle.Comment));

        bool star = n > scanFrom ? row[n - 1] == '*' : starPending && n == 0;
        open = new RawLexState(InBlockComment | (star ? StarPending : 0));
        return false;
    }
}
