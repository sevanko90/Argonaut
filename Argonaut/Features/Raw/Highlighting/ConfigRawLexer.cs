using System;
using System.Collections.Generic;

namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// Colours config files - YAML, INI, <c>.properties</c>, <c>.env</c>, <c>.editorconfig</c>,
/// <c>.gitconfig</c> and simple TOML - with one set of rules, because for colour they differ in
/// what surrounds a pair, not in the pair: a key, a separator and a value. The separator is
/// <c>=</c>, or <c>:</c> followed by a blank or the line's end - the YAML rule, which is also what
/// keeps a URL, a Windows path or a time in a value from reading as one. Around the pairs it
/// recognises the union of the formats' furniture: <c>#</c> comments, and <c>;</c> ones after a
/// blank; <c>;</c> and <c>!</c> comment lines; <c>[section]</c> headers; YAML's document markers,
/// list markers and block scalar markers; and the <c>export</c> prefix <c>.env</c> files allow.
/// The formats barely collide, so the union costs little: a YAML value containing <c> ; </c> reads
/// the rest as a comment.
///
/// Line-local: a block scalar's body, a multi-line flow collection or a multi-line quoted string
/// is coloured by what each of its own rows looks like, and a body line that happens to contain
/// <c>key: </c> reads as a key.
///
/// A row with no <c>key:</c> is value text only, never a key - but its line may still turn out to
/// have one on a later row of a wrapped line, so the row hands on an undecided state; a key cut
/// before its colon is then coloured as a value on the first part. A bare value word that so far
/// reads as a prefix of <c>true</c>, <c>null</c> and the like is coloured as that keyword, which
/// a boundary can leave on the first part of a longer word.
/// </summary>
public sealed class ConfigRawLexer : IRawLexer
{
    // The mode lives in the low four bits; the rest qualify it.
    private const int ModeMask = 0xF;
    private const int AtLineStart = 0;
    private const int InComment = 1;
    private const int Undecided = 2;
    private const int UndecidedAfterText = 3;
    private const int BeforeValue = 4;
    private const int InValueText = 5;
    private const int InQuoted = 6;
    private const int AfterClose = 7;
    private const int InNumber = 8;
    private const int InWord = 9;
    private const int AfterKeyCandidate = 10;
    private const int InSection = 11;

    private const int PreviousBlank = 1 << 4;
    private const int EscapePending = 1 << 5;
    private const int DoubleQuote = 1 << 6;
    private const int KeyCandidate = 1 << 7;    // the open string began where a key would
    private const int WordLengthShift = 8;      // 3 bits: chars of the keyword prefix read so far
    private const int WordCharsShift = 11;      // 4 chars, 5 bits each (a = 1)

    private static readonly string[] Keywords = { "true", "false", "yes", "no", "on", "off", "null" };

    private static RawTextStyle StyleOf(int keyword)
        => Keywords[keyword] == "null" ? RawTextStyle.Literal : RawTextStyle.Keyword;

    public string DisplayName => "Config";

    public RawLexState Lex(ReadOnlySpan<char> row, RawLexState entry, List<RawStyledSpan> spans)
    {
        int n = row.Length;
        if (n == 0)
            return entry;

        int i = 0;
        int mode = entry.Bits & ModeMask;
        bool previousBlank = (entry.Bits & PreviousBlank) != 0;
        int carried = entry.Bits;

        // True while the row is a line's text that has not shown a key yet: whatever it ends in,
        // the next row of the line goes back to looking for the key.
        bool undecided = mode is Undecided or UndecidedAfterText;

        // A list marker makes what follows an item, so a bracket after one is YAML's flow
        // sequence rather than a section header.
        bool listItem = false;

        while (i < n)
        {
            char c = row[i];

            switch (mode)
            {
                case AtLineStart:
                    if (RawLexChars.IsBlank(c))
                    {
                        i++;
                    }
                    else if (c is '#' or ';' or '!')
                    {
                        spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Comment));
                        return Carry(InComment, row);
                    }
                    else if (c == '[' && !listItem)
                    {
                        mode = InSection;
                    }
                    else if (i == 0 && IsDocumentMarker(row))
                    {
                        spans.Add(new RawStyledSpan(0, 3, RawTextStyle.Punctuation));
                        i = 3;
                        mode = Undecided;
                        undecided = true;
                    }
                    else if (c == '-' && (i + 1 == n || RawLexChars.IsBlank(row[i + 1])))
                    {
                        spans.Add(new RawStyledSpan(i, 1, RawTextStyle.Punctuation));
                        i++;
                        listItem = true;
                    }
                    else
                    {
                        if (!listItem && IsExportPrefix(row, i))
                        {
                            spans.Add(new RawStyledSpan(i, 6, RawTextStyle.Keyword));
                            i += 6;
                            while (i < n && RawLexChars.IsBlank(row[i]))
                                i++;
                        }

                        mode = Undecided;
                        undecided = true;
                    }

                    break;

                case InSection:
                {
                    int close = row[i..].IndexOf(']');
                    if (close < 0)
                    {
                        spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Section));
                        return Carry(InSection, row);
                    }

                    int end = i + close + 1;
                    while (end < n && row[end] == ']')
                        end++;

                    spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.Section));
                    i = end;
                    mode = AfterClose;
                    previousBlank = false;
                    break;
                }

                case InComment:
                    spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Comment));
                    return Carry(InComment, row);

                case Undecided:
                case UndecidedAfterText:
                {
                    undecided = true;
                    int next = mode == UndecidedAfterText ? InValueText : BeforeValue;

                    if (c is '"' or '\'')
                    {
                        // A quoted string where a key would start is a key if a colon follows it.
                        bool isDouble = c == '"';
                        int close = ScanQuoted(row, i + 1, isDouble, false, out bool escape);
                        if (close < 0)
                        {
                            spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.String));
                            return Carry(InQuoted | KeyCandidate | (isDouble ? DoubleQuote : 0) | (escape ? EscapePending : 0), row);
                        }

                        i = CloseQuoted(row, i, close, spans, ref mode);
                        previousBlank = false;
                        if (mode == BeforeValue)
                            undecided = false;
                        break;
                    }

                    int separator = FindSeparator(row, i, previousBlank);
                    if (separator >= 0)
                    {
                        int keyEnd = separator;
                        while (keyEnd > i && RawLexChars.IsBlank(row[keyEnd - 1]))
                            keyEnd--;

                        if (keyEnd > i)
                            spans.Add(new RawStyledSpan(i, keyEnd - i, RawTextStyle.Key));
                        spans.Add(new RawStyledSpan(separator, 1, RawTextStyle.Punctuation));
                        i = separator + 1;
                        mode = BeforeValue;
                        undecided = false;
                        previousBlank = false;
                    }
                    else
                    {
                        mode = next;
                    }

                    break;
                }

                case AfterKeyCandidate:
                    if (RawLexChars.IsBlank(c))
                    {
                        previousBlank = true;
                        i++;
                    }
                    else if (IsSeparator(row, i))
                    {
                        spans.Add(new RawStyledSpan(i, 1, RawTextStyle.Punctuation));
                        i++;
                        mode = BeforeValue;
                        previousBlank = false;
                    }
                    else
                    {
                        mode = AfterClose;
                    }

                    break;

                case BeforeValue:
                    if (RawLexChars.IsBlank(c))
                    {
                        previousBlank = true;
                        i++;
                    }
                    else if (c is '#' or ';' && previousBlank)
                    {
                        spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Comment));
                        return Carry(InComment, row);
                    }
                    else if (c is '"' or '\'')
                    {
                        bool isDouble = c == '"';
                        int close = ScanQuoted(row, i + 1, isDouble, false, out bool escape);
                        if (close < 0)
                        {
                            spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.String));
                            return Carry(InQuoted | (isDouble ? DoubleQuote : 0) | (escape ? EscapePending : 0), row);
                        }

                        spans.Add(new RawStyledSpan(i, close - i, RawTextStyle.String));
                        i = close;
                        mode = AfterClose;
                        previousBlank = false;
                    }
                    else if (c is '|' or '>')
                    {
                        int end = i + 1;
                        while (end < n && (row[end] is '+' or '-' || char.IsAsciiDigit(row[end])))
                            end++;

                        if (end == n || RawLexChars.IsBlank(row[end]))
                        {
                            spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.Punctuation));
                            i = end;
                            mode = AfterClose;
                            previousBlank = false;
                        }
                        else
                        {
                            mode = InValueText;
                        }
                    }
                    else if (c == '~' && (i + 1 == n || RawLexChars.IsBlank(row[i + 1])))
                    {
                        spans.Add(new RawStyledSpan(i, 1, RawTextStyle.Literal));
                        i++;
                        mode = InValueText;
                        previousBlank = false;
                    }
                    else if (char.IsAsciiDigit(c) || c is '-' or '+')
                    {
                        mode = InNumber;
                    }
                    else if (char.IsAsciiLetter(c))
                    {
                        int end = i;
                        while (end < n && char.IsAsciiLetter(row[end]))
                            end++;

                        var word = row[i..end];
                        if (end < n)
                        {
                            // The word is over: a keyword only if it stands alone, and otherwise
                            // the start of plain value text, which the next phase colours whole.
                            if (RawLexChars.IsBlank(row[end]) && KeywordIndex(word, exact: true) is int exact and >= 0)
                            {
                                spans.Add(new RawStyledSpan(i, end - i, StyleOf(exact)));
                                i = end;
                                previousBlank = false;
                            }

                            mode = InValueText;
                            break;
                        }

                        // The word reaches the row's end, so it may continue on the next row.
                        if (KeywordIndex(word, exact: true) is int whole and >= 0)
                        {
                            spans.Add(new RawStyledSpan(i, end - i, StyleOf(whole)));
                            return Carry(undecided ? UndecidedAfterText : InValueText, row);
                        }

                        if (KeywordIndex(word, exact: false) is int prefix and >= 0)
                        {
                            spans.Add(new RawStyledSpan(i, end - i, StyleOf(prefix)));
                            return WordProgress(word);
                        }

                        spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.String));
                        return Carry(undecided ? UndecidedAfterText : InValueText, row);
                    }
                    else
                    {
                        mode = InValueText;
                    }

                    break;

                case InValueText:
                    if (RawLexChars.IsBlank(c))
                    {
                        previousBlank = true;
                        i++;
                    }
                    else if (c is '#' or ';' && previousBlank)
                    {
                        spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Comment));
                        return Carry(InComment, row);
                    }
                    else
                    {
                        int end = i;
                        while (end < n && !RawLexChars.IsBlank(row[end]))
                            end++;

                        spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.String));
                        i = end;
                        previousBlank = false;
                    }

                    break;

                case InQuoted:
                {
                    bool isDouble = (carried & DoubleQuote) != 0;
                    int close = ScanQuoted(row, i, isDouble, (carried & EscapePending) != 0, out bool escape);
                    if (close < 0)
                    {
                        spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.String));
                        return Carry(InQuoted | (carried & KeyCandidate) | (isDouble ? DoubleQuote : 0) | (escape ? EscapePending : 0), row);
                    }

                    if ((carried & KeyCandidate) != 0)
                    {
                        i = CloseQuoted(row, i, close, spans, ref mode);
                    }
                    else
                    {
                        spans.Add(new RawStyledSpan(i, close - i, RawTextStyle.String));
                        i = close;
                        mode = AfterClose;
                    }

                    previousBlank = false;
                    break;
                }

                case AfterClose:
                    if (RawLexChars.IsBlank(c))
                    {
                        previousBlank = true;
                    }
                    else if (c is '#' or ';' && previousBlank)
                    {
                        spans.Add(new RawStyledSpan(i, n - i, RawTextStyle.Comment));
                        return Carry(InComment, row);
                    }
                    else
                    {
                        previousBlank = false;
                    }

                    i++;
                    break;

                case InNumber:
                {
                    int end = i;
                    while (end < n && (char.IsAsciiDigit(row[end]) || row[end] is '.' or 'e' or 'E' or '+' or '-' or '_'))
                        end++;

                    if (end < n && !RawLexChars.IsBlank(row[end]))
                    {
                        // More word after the digits - 12:30, 3px, v1.2-beta - so it was never a
                        // number: the whole word is text.
                        while (end < n && !RawLexChars.IsBlank(row[end]))
                            end++;

                        spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.String));
                        i = end;
                        mode = InValueText;
                        previousBlank = false;
                        if (i == n)
                            return Carry(InValueText, row);
                        break;
                    }

                    if (end > i)
                        spans.Add(new RawStyledSpan(i, end - i, RawTextStyle.Number));
                    i = end;
                    mode = InValueText;
                    previousBlank = false;
                    if (i == n)
                        return Carry(InNumber, row);
                    break;
                }

                case InWord:
                {
                    int end = i;
                    while (end < n && char.IsAsciiLetter(row[end]))
                        end++;

                    if (end == i)
                    {
                        mode = InValueText;
                        break;
                    }

                    // Spliced back together with what the previous row read, is it a keyword?
                    Span<char> joined = stackalloc char[8];
                    int length = (carried >> WordLengthShift) & 7;
                    for (int k = 0; k < length; k++)
                        joined[k] = (char)('a' + ((carried >> (WordCharsShift + 5 * k)) & 31) - 1);

                    bool fits = length + (end - i) <= joined.Length;
                    if (fits)
                        row[i..end].CopyTo(joined[length..]);
                    var whole = fits ? joined[..(length + end - i)] : default;

                    if (fits && end == n && KeywordIndex(whole, exact: false) is int prefix and >= 0)
                    {
                        spans.Add(new RawStyledSpan(i, end - i, StyleOf(prefix)));
                        return WordProgress(whole);
                    }

                    bool wordEnds = end == n || RawLexChars.IsBlank(row[end]);
                    int keyword = fits && wordEnds ? KeywordIndex(whole, exact: true) : -1;
                    spans.Add(new RawStyledSpan(i, end - i, keyword >= 0 ? StyleOf(keyword) : RawTextStyle.String));
                    i = end;
                    mode = InValueText;
                    previousBlank = false;
                    if (i == n)
                        return Carry(InValueText, row);
                    break;
                }
            }
        }

        if (undecided)
        {
            if (mode == BeforeValue)
                mode = Undecided;
            else if (mode == InValueText)
                mode = UndecidedAfterText;
        }

        return Carry(mode, row);
    }

    /// <summary>
    /// Colours a string that closed at <paramref name="close"/> where a key could stand: a key
    /// when a separator follows it, a plain string otherwise. Returns the index to carry on from.
    /// </summary>
    private static int CloseQuoted(ReadOnlySpan<char> row, int start, int close, List<RawStyledSpan> spans, ref int mode)
    {
        int k = close;
        while (k < row.Length && RawLexChars.IsBlank(row[k]))
            k++;

        if (k < row.Length && IsSeparator(row, k))
        {
            spans.Add(new RawStyledSpan(start, close - start, RawTextStyle.Key));
            spans.Add(new RawStyledSpan(k, 1, RawTextStyle.Punctuation));
            mode = BeforeValue;
            return k + 1;
        }

        // Nothing but blanks after the quote: the separator may open the next row.
        spans.Add(new RawStyledSpan(start, close - start, RawTextStyle.String));
        mode = k == row.Length ? AfterKeyCandidate : AfterClose;
        return close;
    }

    /// <summary>
    /// The separator that makes the text from <paramref name="from"/> a key - the first one,
    /// before any comment. -1 when there is none on this row.
    /// </summary>
    private static int FindSeparator(ReadOnlySpan<char> row, int from, bool previousBlank)
    {
        for (int k = from; k < row.Length; k++)
        {
            if (row[k] == '#' && (k > from ? RawLexChars.IsBlank(row[k - 1]) : previousBlank))
                return -1;

            if (IsSeparator(row, k))
                return k;
        }

        return -1;
    }

    /// <summary>
    /// True at <c>=</c>, or at a <c>:</c> followed by a blank or the row's end. A colon with text
    /// straight after it is part of a word - <c>http://</c>, <c>C:\</c>, <c>12:30</c>.
    /// </summary>
    private static bool IsSeparator(ReadOnlySpan<char> row, int at)
        => row[at] == '=' || (row[at] == ':' && (at + 1 == row.Length || RawLexChars.IsBlank(row[at + 1])));

    /// <summary>True at <c>export</c> followed by a blank, the prefix <c>.env</c> files allow.</summary>
    private static bool IsExportPrefix(ReadOnlySpan<char> row, int at)
        => row.Length > at + 6
           && row.Slice(at, 6).Equals("export", StringComparison.Ordinal)
           && RawLexChars.IsBlank(row[at + 6]);

    /// <summary>True at <c>---</c> or <c>...</c> standing alone at the start of a row.</summary>
    private static bool IsDocumentMarker(ReadOnlySpan<char> row)
        => row.Length >= 3
           && (row.StartsWith("---", StringComparison.Ordinal) || row.StartsWith("...", StringComparison.Ordinal))
           && (row.Length == 3 || RawLexChars.IsBlank(row[3]));

    /// <summary>
    /// The state to hand the next row: <paramref name="state"/>, plus whether this row ended in a
    /// blank, which is what a comment marker at the start of the next row is measured against.
    /// </summary>
    private static RawLexState Carry(int state, ReadOnlySpan<char> row)
        => new(state | (RawLexChars.IsBlank(row[^1]) ? PreviousBlank : 0));

    /// <summary>The state for a bare word still being read, remembering the letters so far.</summary>
    private static RawLexState WordProgress(ReadOnlySpan<char> word)
    {
        int bits = InWord | (word.Length << WordLengthShift);
        for (int k = 0; k < word.Length; k++)
            bits |= (char.ToLowerInvariant(word[k]) - 'a' + 1) << (WordCharsShift + 5 * k);
        return new RawLexState(bits);
    }

    private static int KeywordIndex(ReadOnlySpan<char> word, bool exact)
    {
        for (int k = 0; k < Keywords.Length; k++)
        {
            var keyword = Keywords[k].AsSpan();
            if (exact
                    ? word.Equals(keyword, StringComparison.OrdinalIgnoreCase)
                    : word.Length < keyword.Length && keyword.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return k;
        }

        return -1;
    }

    /// <summary>
    /// The index just past the closing quote at or after <paramref name="from"/>, or -1 when the
    /// string is still open at the row's end. Only a double-quoted string honours backslashes.
    /// </summary>
    private static int ScanQuoted(ReadOnlySpan<char> row, int from, bool isDouble, bool escapePending, out bool escape)
    {
        escape = escapePending;
        char quote = isDouble ? '"' : '\'';
        for (int i = from; i < row.Length; i++)
        {
            if (escape)
                escape = false;
            else if (isDouble && row[i] == '\\')
                escape = true;
            else if (row[i] == quote)
                return i + 1;
        }

        return -1;
    }
}
