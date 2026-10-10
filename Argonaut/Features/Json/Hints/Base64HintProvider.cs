using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A chip for a Base64 string saying what it decodes to and how big: "PNG 32×32 · 3 MB",
/// "gzip · 1.2 KB", or a preview of the text it holds.
///
/// Only the first bytes are decoded, so a megabyte value costs what a short one does, and its
/// decoded size is arithmetic on its length. Plenty of ordinary strings are made of Base64's
/// letters - words, identifiers, hex hashes - so being in the alphabet is not enough: what the
/// first bytes decode to must be a file type recognised by its signature, or readable text.
/// Binary with no signature gets nothing, rather than a guess.
/// </summary>
public sealed class Base64HintProvider : IValueHintProvider
{
    /// <summary>Shorter than this and a string is more likely a word than an encoding.</summary>
    private const int MinLength = 16;

    /// <summary>How much of the value is decoded: enough for any signature and a preview.</summary>
    private const int DecodedChars = 96;

    private const int PreviewChars = 24;

    private enum Payload : byte { Text, Png, Jpeg, Gif, WebP, Pdf, Zip, Gzip }

    public bool IsActive => true;

    public bool ReadsPrefixes => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || valueLength < MinLength || !IsBase64(rawValue, valueLength == rawValue.Length, out bool isUrlSafe))
            return false;

        Span<byte> decoded = stackalloc byte[DecodedChars / 4 * 3];
        int length = Decode(rawValue, isUrlSafe, decoded);
        if (Recognise(decoded[..length]) is not { } payload)
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Base64, DecodedLength(rawValue, valueLength), (byte)payload, isUrlSafe ? (short)1 : (short)0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset)
    {
        Span<byte> decoded = stackalloc byte[DecodedChars / 4 * 3];
        var head = decoded[..Decode(rawValue, candidate.OffsetMinutes == 1, decoded)];
        string size = ByteLengthText.Format(candidate.Payload);
        var payload = (Payload)candidate.SchemeHint;
        return payload switch
        {
            Payload.Text => new ValueHint($"Base64 text · “{Preview(head)}” · {size}", TreeRunIcon.Binary),
            Payload.Png when head.Length >= 24 =>
                new ValueHint($"PNG {BigEndian(head[16..20])}×{BigEndian(head[20..24])} · {size}", TreeRunIcon.Image),
            Payload.Gif when head.Length >= 10 =>
                new ValueHint($"GIF {head[6] | head[7] << 8}×{head[8] | head[9] << 8} · {size}", TreeRunIcon.Image),
            Payload.Png or Payload.Gif or Payload.Jpeg or Payload.WebP => new ValueHint($"{payload.ToString().ToUpperInvariant()} image · {size}", TreeRunIcon.Image),
            Payload.Pdf => new ValueHint($"PDF · {size}", TreeRunIcon.Binary),
            Payload.Zip => new ValueHint($"ZIP · {size}", TreeRunIcon.Binary),
            _ => new ValueHint($"gzip · {size}", TreeRunIcon.Binary),
        };
    }

    /// <summary>Every byte in one Base64 alphabet - standard or URL-safe, not a mix - and, for a
    /// whole value, a length and padding that decode; not a run of hex digits, which is far more
    /// often a hash.</summary>
    private static bool IsBase64(ReadOnlySpan<byte> text, bool isWhole, out bool isUrlSafe)
    {
        isUrlSafe = false;
        bool standard = false, urlSafe = false, onlyHex = true;
        int padding = 0;
        foreach (byte b in text)
        {
            if (padding > 0 && b != '=')
                return false;
            switch (b)
            {
                case >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F':
                    break;
                case >= (byte)'g' and <= (byte)'z' or >= (byte)'G' and <= (byte)'Z':
                    onlyHex = false;
                    break;
                case (byte)'+' or (byte)'/':
                    standard = true;
                    onlyHex = false;
                    break;
                case (byte)'-' or (byte)'_':
                    urlSafe = true;
                    onlyHex = false;
                    break;
                case (byte)'=':
                    padding++;
                    break;
                default:
                    return false;
            }
        }

        if (onlyHex || (standard && urlSafe) || padding > 2)
            return false;

        isUrlSafe = urlSafe;
        return !isWhole || (urlSafe ? padding == 0 && text.Length % 4 != 1 : text.Length % 4 == 0 || (padding == 0 && text.Length % 4 != 1));
    }

    /// <summary>Decodes the start of the value into <paramref name="decoded"/>; returns how many
    /// bytes it holds.</summary>
    private static int Decode(ReadOnlySpan<byte> text, bool isUrlSafe, Span<byte> decoded)
    {
        var head = text[..Math.Min(text.Length, DecodedChars)];
        head = head[..(head.Length / 4 * 4)].TrimEnd((byte)'=');
        head = head[..(head.Length / 4 * 4)];
        var status = isUrlSafe
            ? Base64Url.DecodeFromUtf8(head, decoded, out _, out int written, isFinalBlock: false)
            : Base64.DecodeFromUtf8(head, decoded, out _, out written, isFinalBlock: false);
        return status is OperationStatus.Done or OperationStatus.NeedMoreData ? written : 0;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> GzipSignature => [0x1F, 0x8B, 0x08];

    private static Payload? Recognise(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature))
            return Payload.Png;
        if (head.StartsWith(JpegSignature))
            return Payload.Jpeg;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return Payload.Gif;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return Payload.WebP;
        if (head.StartsWith("%PDF-"u8))
            return Payload.Pdf;
        if (head.StartsWith(ZipSignature))
            return Payload.Zip;
        if (head.StartsWith(GzipSignature))
            return Payload.Gzip;
        return IsReadable(head) ? Payload.Text : null;
    }

    /// <summary>At least a few bytes of UTF-8 with no control characters but whitespace - a cut
    /// character at the very end allowed, since only the start was decoded.</summary>
    private static bool IsReadable(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8)
            return false;

        for (int trim = 0; trim <= 3 && trim < head.Length; trim++)
        {
            var text = head[..^trim];
            if (!System.Text.Unicode.Utf8.IsValid(text))
                continue;
            foreach (byte b in text)
            {
                if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r') || b == 0x7F)
                    return false;
            }

            return true;
        }

        return false;
    }

    private static string Preview(ReadOnlySpan<byte> head)
    {
        string text = Encoding.UTF8.GetString(head).ReplaceLineEndings(" ").TrimEnd('\uFFFD');
        if (text.Length <= PreviewChars)
            return text;

        // At a word break where there is one, so the preview does not end mid-word.
        int cut = text.LastIndexOf(' ', PreviewChars);
        return text[..(cut > PreviewChars / 2 ? cut : PreviewChars)].TrimEnd() + "…";
    }

    /// <summary>How many bytes the whole value decodes to - exact for a whole value, from its
    /// length alone for a longer one, whose padding is not in the prefix.</summary>
    private static long DecodedLength(ReadOnlySpan<byte> text, long valueLength)
    {
        int padding = valueLength == text.Length ? text.Length - text.TrimEnd((byte)'=').Length : 0;
        return (valueLength - padding) * 3 / 4;
    }

    private static uint BigEndian(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);

    /// <summary>What a value decodes to does not depend on any setting.</summary>
    public event EventHandler? HintsChanged
    {
        add { }
        remove { }
    }
}
