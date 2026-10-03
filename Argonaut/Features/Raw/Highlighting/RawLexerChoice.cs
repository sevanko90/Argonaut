using System;
using System.Collections.Generic;
using System.IO;

namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// Picks the lexer a raw document is coloured with: from the file's name when it has one, from
/// its first lines otherwise. Pure functions over strings, so they need no window to test.
/// </summary>
public static class RawLexerChoice
{
    /// <summary>The most line starts <see cref="Sniff"/> looks at; a caller need not offer more.</summary>
    public const int MaxSniffedLines = 50;

    /// <summary>Share of the counted lines that must look like a pair for a config file to be guessed.</summary>
    private const double SniffThreshold = 0.6;

    public static IRawLexer Json { get; } = new JsonRawLexer();

    public static IRawLexer Config { get; } = new ConfigRawLexer();

    /// <summary>The lexer a picker choice means; <paramref name="auto"/> is what Auto resolved to.</summary>
    public static IRawLexer? For(RawColourChoice choice, IRawLexer? auto) => choice switch
    {
        RawColourChoice.Auto => auto,
        RawColourChoice.Json => Json,
        RawColourChoice.Config => Config,
        _ => null,
    };

    /// <summary>The lexer for a file name, or null when the name says nothing.</summary>
    public static IRawLexer? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string name = Path.GetFileName(path);
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            return Config;

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".json" or ".jsonc" or ".json5" or ".geojson" or ".ndjson" or ".jsonl" => Json,
            ".ini" or ".cfg" or ".conf" or ".properties" or ".toml" or ".editorconfig" or ".gitconfig"
                or ".yaml" or ".yml" => Config,
            _ => null,
        };
    }

    /// <summary>
    /// Guesses a lexer from the text of the first <see cref="MaxSniffedLines"/> rows that begin a
    /// line. JSON is decided by how the first line opens; otherwise it is a config file when at
    /// least 60% of the lines that are neither blank, comments, nor <c>[section]</c> headers read
    /// as <c>key = value</c> or <c>key: value</c> pairs.
    /// </summary>
    public static IRawLexer? Sniff(IReadOnlyList<string> lineStarts)
    {
        int count = Math.Min(lineStarts.Count, MaxSniffedLines);
        int pairs = 0, counted = 0;
        bool first = true;

        for (int i = 0; i < count; i++)
        {
            var line = lineStarts[i].AsSpan().Trim(" \t␉");
            if (line.IsEmpty)
                continue;

            if (first)
            {
                first = false;
                if (OpensJson(line))
                    return Json;
            }

            if (IsComment(line) || IsSection(line) || line is "---" or "...")
                continue;

            counted++;
            if (IsEqualsPair(line) || IsColonPair(line))
                pairs++;
        }

        return counted > 0 && pairs >= counted * SniffThreshold ? Config : null;
    }

    /// <summary>
    /// A line that opens a JSON value: an object, or an array that goes on to hold a value or end.
    /// A <c>[</c> alone is an array whose elements follow on the next lines - it cannot be a
    /// section header, which closes its bracket.
    /// </summary>
    private static bool OpensJson(ReadOnlySpan<char> line)
    {
        if (line[0] == '{')
            return true;

        if (line[0] != '[')
            return false;

        var rest = line[1..].TrimStart(" \t␉");
        return rest.IsEmpty || rest[0] is '{' or '[' or '"' or ']' || char.IsAsciiDigit(rest[0]);
    }

    private static bool IsComment(ReadOnlySpan<char> line)
        => line[0] is '#' or ';' or '!' || line.StartsWith("//", StringComparison.Ordinal);

    private static bool IsSection(ReadOnlySpan<char> line) => line[0] == '[' && line[^1] == ']';

    /// <summary><c>key = value</c>: an equals sign after a key.</summary>
    private static bool IsEqualsPair(ReadOnlySpan<char> line)
    {
        int equals = line.IndexOf('=');
        return equals > 0 && !line[..equals].TrimEnd(" \t␉").IsEmpty;
    }

    /// <summary><c>key: value</c> or <c>key:</c>, past any list markers.</summary>
    private static bool IsColonPair(ReadOnlySpan<char> line)
    {
        while (line.StartsWith("- ", StringComparison.Ordinal))
            line = line[2..].TrimStart(" \t␉");

        return !line.IsEmpty && ColonPairIndex(line) > 0;
    }

    /// <summary>The first colon that is followed by a blank or the end, or -1.</summary>
    private static int ColonPairIndex(ReadOnlySpan<char> line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ':' && (i + 1 == line.Length || line[i + 1] is ' ' or '\t' or '␉'))
                return i;
        }

        return -1;
    }
}
