using System;
using System.Buffers.Text;
using System.Text.Json;
using Argonaut.Engine.Text;

namespace Argonaut.Features.Json.Hints;

/// <summary>What a hint's value decodes to, for showing in full: a JSON document, text, a
/// picture, or other bytes - with a title saying what it is.</summary>
public enum ExpandedKind : byte { Json, Text, Image, Binary }

public sealed record ExpandedValue(ExpandedKind Kind, byte[] Bytes, string Title);

/// <summary>
/// Decodes the value behind a hint that hides a document - a JWT's header and claims, JSON
/// inside a string, Base64's bytes - when it is asked to be expanded, never when the row is drawn.
/// Pure, so it runs off the UI thread; null when the value does not decode after all.
/// </summary>
public static class HintExpansion
{
    /// <summary>Most raw value bytes decoded for a preview. Past it, a value is offered to the raw
    /// view instead: decoding it would hold it twice in memory, and nothing reads that much in a
    /// card.</summary>
    public const int MaxValueBytes = 8 * 1024 * 1024;

    /// <param name="rawValue">A string's content as it is in the file, escapes and all.</param>
    public static ExpandedValue? Decode(ValueHintKind kind, ReadOnlySpan<byte> rawValue) => kind switch
    {
        ValueHintKind.Jwt => DecodeJwt(rawValue),
        ValueHintKind.EmbeddedJson => DecodeEmbeddedJson(rawValue),
        ValueHintKind.Base64 => DecodeBase64(rawValue),
        _ => null,
    };

    /// <summary>The header and claims as one document: <c>{"header": …, "claims": …}</c>.</summary>
    private static ExpandedValue? DecodeJwt(ReadOnlySpan<byte> token)
    {
        int first = token.IndexOf((byte)'.');
        int second = first < 0 ? -1 : first + 1 + token[(first + 1)..].IndexOf((byte)'.');
        if (first < 0 || second <= first)
            return null;

        try
        {
            byte[] header = Base64Url.DecodeFromUtf8(token[..first]);
            byte[] claims = Base64Url.DecodeFromUtf8(token[(first + 1)..second]);
            if (!IsJson(header) || !IsJson(claims))
                return null;

            byte[] document = [.. "{\"header\":"u8, .. header, .. ",\"claims\":"u8, .. claims, .. "}"u8];
            return new ExpandedValue(ExpandedKind.Json, document, "JWT · header and claims");
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ExpandedValue? DecodeEmbeddedJson(ReadOnlySpan<byte> rawValue)
    {
        var quoted = new byte[rawValue.Length + 2];
        quoted[0] = quoted[^1] = (byte)'"';
        rawValue.CopyTo(quoted.AsSpan(1));
        try
        {
            var reader = new Utf8JsonReader(quoted);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                return null;

            var content = new byte[rawValue.Length];
            byte[] document = content[..reader.CopyString(content)];
            return IsJson(document) ? new ExpandedValue(ExpandedKind.Json, document, "JSON in a string") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExpandedValue? DecodeBase64(ReadOnlySpan<byte> rawValue)
    {
        byte[] bytes;
        if (rawValue.IndexOfAny("-_"u8) >= 0)
        {
            try
            {
                bytes = Base64Url.DecodeFromUtf8(rawValue);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        else if (DecodeStandard(rawValue) is { } decoded)
        {
            bytes = decoded;
        }
        else
        {
            return null;
        }

        string size = ByteLengthText.Format(bytes.Length);
        if (ImageKind(bytes) is { } image)
            return new ExpandedValue(ExpandedKind.Image, bytes, $"{image} · {size}");
        if (System.Text.Unicode.Utf8.IsValid(bytes))
            return new ExpandedValue(IsJson(bytes) ? ExpandedKind.Json : ExpandedKind.Text, bytes, $"Base64 text · {size}");
        return new ExpandedValue(ExpandedKind.Binary, bytes, $"Base64 · {size}");
    }

    /// <summary>Standard Base64, padding supplied where the value left it off.</summary>
    private static byte[]? DecodeStandard(ReadOnlySpan<byte> text)
    {
        int padding = (4 - text.Length % 4) % 4;
        if (padding == 3)
            return null;

        byte[] padded = new byte[text.Length + padding];
        text.CopyTo(padded);
        padded.AsSpan(text.Length).Fill((byte)'=');

        byte[] decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(padded.Length)];
        return Base64.DecodeFromUtf8(padded, decoded, out _, out int written) == System.Buffers.OperationStatus.Done
            ? decoded[..written]
            : null;
    }

    private static string? ImageKind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "PNG";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "JPEG";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "GIF";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "WebP";
        return null;
    }

    /// <summary>Whether the bytes are one whole JSON object or array.</summary>
    private static bool IsJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
