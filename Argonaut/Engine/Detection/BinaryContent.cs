using System;

namespace Argonaut.Engine.Detection;

/// <summary>
/// Whether a document is binary - bytes no text view can draw - decided from what its source said
/// it is (a response's media type) and from its first bytes. Reads only a prefix, so deciding is
/// as cheap for a multi-gigabyte file as for a small one.
/// </summary>
public static class BinaryContent
{
    /// <summary>How much of the start of a document the sniff reads.</summary>
    public const int SniffLength = 8 * 1024;

    /// <summary>
    /// What a media type (<c>Content-Type</c> without parameters) says: true for a binary format
    /// (images other than SVG, audio, video, fonts, archives, PDF), false for a text one, null when
    /// it does not tell - <c>application/octet-stream</c>, which servers send for JSON too, or a
    /// type not known here - and the bytes decide.
    /// </summary>
    public static bool? IsBinaryMediaType(string? mediaType)
    {
        string type = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0 || type == "application/octet-stream")
            return null;

        if (type.StartsWith("text/", StringComparison.Ordinal)
            || type.EndsWith("+json", StringComparison.Ordinal)
            || type.EndsWith("+xml", StringComparison.Ordinal)
            || type is "application/json" or "application/xml" or "application/javascript" or "application/x-www-form-urlencoded"
                or "application/yaml" or "application/x-yaml" or "application/x-ndjson" or "application/ndjson"
                or "application/jsonl" or "application/x-jsonlines" or "application/jsonlines" or "application/graphql"
                or "application/sql")
            return false;

        if (type.StartsWith("image/", StringComparison.Ordinal)
            || type.StartsWith("audio/", StringComparison.Ordinal)
            || type.StartsWith("video/", StringComparison.Ordinal)
            || type.StartsWith("font/", StringComparison.Ordinal)
            || type is "application/pdf" or "application/zip" or "application/gzip" or "application/x-gzip"
                or "application/x-tar" or "application/x-7z-compressed" or "application/vnd.rar" or "application/x-rar-compressed"
                or "application/zstd" or "application/x-bzip2" or "application/wasm" or "application/x-protobuf"
                or "application/protobuf" or "application/x-msgpack" or "application/msgpack" or "application/cbor"
                or "application/vnd.sqlite3" or "application/x-sqlite3" or "application/msword" or "application/vnd.ms-excel"
            || type.StartsWith("application/vnd.openxmlformats-", StringComparison.Ordinal))
            return true;

        return null;
    }

    /// <summary>
    /// Whether a document starting with <paramref name="start"/> is binary: it opens with a known
    /// binary format's signature, or holds a NUL byte - which no UTF-8 text does - without the
    /// byte-order mark of a UTF-16 or UTF-32 file, whose NULs are its characters' high bytes.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> start)
    {
        if (start.IsEmpty)
            return false;
        if (FormatOf(start) is not null)
            return true;
        if (start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || start.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])
            || start.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            return false;

        return start[..Math.Min(start.Length, SniffLength)].IndexOf((byte)0) >= 0;
    }

    /// <summary>The format a document's signature names ("PNG image"), or null when it opens
    /// with none known here. Only signatures specific enough that no text file starts with them.</summary>
    public static string? FormatOf(ReadOnlySpan<byte> start)
    {
        if (start.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "PNG image";
        if (start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "JPEG image";
        if (start.StartsWith("GIF87a"u8) || start.StartsWith("GIF89a"u8))
            return "GIF image";
        if (start.Length >= 12 && start.StartsWith("RIFF"u8) && start[8..12].SequenceEqual("WEBP"u8))
            return "WebP image";
        if (start.Length >= 12 && start.StartsWith("RIFF"u8) && start[8..12].SequenceEqual("WAVE"u8))
            return "WAV audio";
        if (start.StartsWith("II*\0"u8) || start.StartsWith("MM\0*"u8))
            return "TIFF image";
        if (start.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
            return "Icon";
        if (start.StartsWith("%PDF-"u8))
            return "PDF document";
        if (start.StartsWith("PK\x03\x04"u8))
            return "ZIP archive";
        if (start.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
            return "Gzip archive";
        if (start.StartsWith((ReadOnlySpan<byte>)[(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C]))
            return "7-Zip archive";
        if (start.StartsWith("Rar!\x1A\x07"u8))
            return "RAR archive";
        if (start.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]))
            return "Zstandard archive";
        if (start.Length >= 12 && start[4..8].SequenceEqual("ftyp"u8))
            return "MP4 media";
        if (start.StartsWith("OggS"u8))
            return "Ogg media";
        if (start.StartsWith("fLaC"u8))
            return "FLAC audio";
        if (start.StartsWith("SQLite format 3\0"u8))
            return "SQLite database";
        if (start.StartsWith((ReadOnlySpan<byte>)[0x00, (byte)'a', (byte)'s', (byte)'m']))
            return "WebAssembly module";
        if (start.StartsWith((ReadOnlySpan<byte>)[0x7F, (byte)'E', (byte)'L', (byte)'F']))
            return "ELF executable";
        return null;
    }
}
