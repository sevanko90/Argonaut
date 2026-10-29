using System;
using System.Text;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Xml;

/// <summary>
/// The encoding an XML declaration names, read from the start of a document. The viewer decodes
/// UTF-8 whatever the declaration says; this is what lets the status line say so.
/// </summary>
internal static class XmlDeclaredEncoding
{
    /// <summary>How much of the start is looked at: a declaration is a few dozen bytes.</summary>
    private const int Reach = 512;

    /// <summary>The <c>encoding</c> the declaration at the start of <paramref name="bytes"/>
    /// names, or null when there is no declaration or it names none.</summary>
    public static string? Read(IByteSource bytes)
    {
        var span = bytes.GetContiguousSpan(0, Reach);
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];

        ReadOnlySpan<byte> open = "<?xml"u8;
        if (!span.StartsWith(open) || span.Length <= open.Length || !IsSpace(span[open.Length]))
            return null;

        int end = span.IndexOf("?>"u8);
        var declaration = end < 0 ? span : span[..end];

        int at = declaration.IndexOf("encoding"u8);
        if (at < 0)
            return null;

        at += "encoding".Length;
        while (at < declaration.Length && IsSpace(declaration[at]))
            at++;

        if (at >= declaration.Length || declaration[at] != (byte)'=')
            return null;

        at++;
        while (at < declaration.Length && IsSpace(declaration[at]))
            at++;

        if (at >= declaration.Length || declaration[at] is not ((byte)'"' or (byte)'\''))
            return null;

        byte quote = declaration[at++];
        int close = declaration[at..].IndexOf(quote);
        return close < 0 ? null : Encoding.ASCII.GetString(declaration.Slice(at, close));
    }

    /// <summary>Whether the declaration names something other than UTF-8 or US-ASCII, which
    /// decode the same.</summary>
    public static bool IsNotUtf8(string? declared)
        => declared is not null
           && !declared.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
           && !declared.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
