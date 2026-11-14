using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Argonaut.Engine.Http;

/// <summary>One query argument as the user reads it: decoded, with a null value for a bare
/// flag (<c>?debug</c>) and an empty one for <c>?empty=</c>.</summary>
public sealed record QueryArgument(string Key, string? Value);

/// <summary>An argument and the text it has in the URL - as typed for one parsed from a URL,
/// minimally encoded for one made with <see cref="UrlQuery.Part"/>.</summary>
public sealed record QueryPart(QueryArgument Argument, string Raw);

/// <summary>
/// A URL taken apart into what the request pane edits: the base (everything before the query),
/// the query's arguments in order, and the fragment. Immutable - every edit returns a new one.
///
/// Lossless where the user has not edited: each argument keeps the text it had in the URL, so
/// writing the URL back reproduces what was typed or pasted - its own escaping, its <c>+</c>
/// spaces - and an edit rewrites only the argument it touched. A new or edited argument is
/// encoded minimally: only what would break the query (<c>&amp; = + # %</c>, spaces, controls,
/// characters URLs cannot hold, and anything non-ASCII) is escaped, so <c>2024-01-01T10:00:00Z</c>
/// stays readable. Repeated keys, bare flags and order all survive.
///
/// Parses any text, URL or not yet - the pane parses on every keystroke. Empty segments
/// (<c>a=1&amp;&amp;b=2</c>) are not arguments, and malformed escapes are shown as typed.
/// </summary>
public sealed class UrlQuery
{
    private readonly (QueryArgument Argument, string Raw)[] entries;

    private UrlQuery(string @base, (QueryArgument, string)[] entries, string? fragment)
    {
        Base = @base;
        this.entries = entries;
        Fragment = fragment;
    }

    /// <summary>Everything before the query: scheme, host, path.</summary>
    public string Base { get; }

    /// <summary>The text after <c>#</c>, or null when there is none.</summary>
    public string? Fragment { get; }

    public IReadOnlyList<QueryArgument> Arguments => entries.Select(static e => e.Argument).ToArray();

    /// <summary>The arguments with the text each has in the URL, for an editor that keeps them.</summary>
    public IReadOnlyList<QueryPart> Parts => entries.Select(static e => new QueryPart(e.Argument, e.Raw)).ToArray();

    /// <summary>The same base and fragment with <paramref name="parts"/> as the query.</summary>
    public UrlQuery WithParts(IEnumerable<QueryPart> parts)
        => new(Base, parts.Select(static p => (p.Argument, p.Raw)).ToArray(), Fragment);

    /// <summary><paramref name="argument"/> as a new part, encoded only where it must be.</summary>
    public static QueryPart Part(QueryArgument argument) => new(argument, Encode(argument));

    public static UrlQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? fragment = null;
        int hash = text.IndexOf('#');
        if (hash >= 0)
        {
            fragment = text[(hash + 1)..];
            text = text[..hash];
        }

        int question = text.IndexOf('?');
        if (question < 0)
            return new UrlQuery(text, [], fragment);

        var entries = new List<(QueryArgument, string)>();
        foreach (string raw in text[(question + 1)..].Split('&'))
        {
            if (raw.Length == 0)
                continue;

            int equals = raw.IndexOf('=');
            var argument = equals < 0
                ? new QueryArgument(Decode(raw), null)
                : new QueryArgument(Decode(raw[..equals]), Decode(raw[(equals + 1)..]));
            entries.Add((argument, raw));
        }

        return new UrlQuery(text[..question], entries.ToArray(), fragment);
    }

    /// <summary>The query with argument <paramref name="index"/> replaced - the only part of the
    /// URL written anew.</summary>
    public UrlQuery WithArgument(int index, QueryArgument argument)
    {
        var next = (((QueryArgument, string)[])entries.Clone());
        next[index] = (argument, Encode(argument));
        return new UrlQuery(Base, next, Fragment);
    }

    public UrlQuery WithAdded(QueryArgument argument)
        => new(Base, [.. entries, (argument, Encode(argument))], Fragment);

    public UrlQuery WithRemoved(int index)
        => new(Base, entries.Where((_, i) => i != index).ToArray(), Fragment);

    /// <summary>The URL: the base, <c>?</c> and the arguments when there are any, then the
    /// fragment.</summary>
    public override string ToString()
    {
        var url = new StringBuilder(Base);
        if (entries.Length > 0)
            url.Append('?').AppendJoin('&', entries.Select(static e => e.Raw));
        if (Fragment is not null)
            url.Append('#').Append(Fragment);
        return url.ToString();
    }

    private static string Encode(QueryArgument argument)
        => argument.Value is null ? EncodePart(argument.Key) : EncodePart(argument.Key) + "=" + EncodePart(argument.Value);

    private static string EncodePart(string text)
    {
        var encoded = new StringBuilder(text.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80 && !NeedsEscape(c))
            {
                encoded.Append(c);
                continue;
            }

            int length = char.IsHighSurrogate(c) && i + 1 < text.Length
                ? Encoding.UTF8.GetBytes(text.AsSpan(i++, 2), utf8)
                : Encoding.UTF8.GetBytes(text.AsSpan(i, 1), utf8);
            for (int b = 0; b < length; b++)
                encoded.Append('%').Append(utf8[b].ToString("X2"));
        }

        return encoded.ToString();
    }

    // What would end an argument, change its meaning, or is not allowed in a URL at all.
    private static bool NeedsEscape(char c)
        => c <= 0x20 || c == 0x7F || c is '&' or '=' or '+' or '#' or '%' or '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}';

    /// <summary>Query decoding: <c>+</c> is a space, and each valid <c>%XX</c> a byte of UTF-8.
    /// An escape that is not one is kept as typed rather than refused.</summary>
    private static string Decode(string raw)
    {
        if (raw.IndexOfAny(['%', '+']) < 0)
            return raw;

        var bytes = new List<byte>(raw.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '%' && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
            {
                bytes.Add(Convert.ToByte(raw.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                int length = char.IsHighSurrogate(c) && i + 1 < raw.Length
                    ? Encoding.UTF8.GetBytes(raw.AsSpan(i++, 2), utf8)
                    : Encoding.UTF8.GetBytes(raw.AsSpan(i, 1), utf8);
                for (int b = 0; b < length; b++)
                    bytes.Add(utf8[b]);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}
