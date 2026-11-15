using System;
using System.IO;
using System.Text;

namespace Argonaut.Engine.Http;

/// <summary>
/// What a response is called when the user saves a copy of it: the request's name made safe for a
/// file system, with the extension its media type implies. The kept file itself is always
/// <c>.response</c> in the response folder; this is only the name offered in a save dialog.
/// </summary>
public static class ResponseFileNames
{
    /// <summary>The extension, with its dot, for a media type (<c>Content-Type</c> without its
    /// parameters). Structured-syntax suffixes count (<c>application/geo+json</c> is
    /// <c>.json</c>); a media type not known here is <c>.txt</c> for text and <c>.bin</c> for
    /// anything else; no media type at all is <c>.txt</c>, as the views read it as text.</summary>
    public static string ExtensionFor(string? mediaType)
    {
        string type = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0)
            return ".txt";

        switch (type)
        {
            case "application/json": return ".json";
            case "application/x-ndjson" or "application/ndjson" or "application/jsonl" or "application/x-jsonlines" or "application/jsonlines": return ".ndjson";
            case "text/csv": return ".csv";
            case "text/tab-separated-values": return ".tsv";
            case "application/xml" or "text/xml": return ".xml";
            case "text/html": return ".html";
            case "text/plain": return ".txt";
            case "text/markdown": return ".md";
            case "text/css": return ".css";
            case "text/javascript" or "application/javascript": return ".js";
            case "application/yaml" or "application/x-yaml" or "text/yaml": return ".yaml";
            case "image/svg+xml": return ".svg";
            case "image/png": return ".png";
            case "image/jpeg": return ".jpg";
            case "image/gif": return ".gif";
            case "image/webp": return ".webp";
            case "image/bmp": return ".bmp";
            case "image/x-icon" or "image/vnd.microsoft.icon": return ".ico";
            case "application/pdf": return ".pdf";
            case "application/zip": return ".zip";
            case "application/gzip" or "application/x-gzip": return ".gz";
            case "application/octet-stream": return ".bin";
        }

        if (type.EndsWith("+json", StringComparison.Ordinal))
            return ".json";
        if (type.EndsWith("+xml", StringComparison.Ordinal))
            return ".xml";
        return type.StartsWith("text/", StringComparison.Ordinal) ? ".txt" : ".bin";
    }

    /// <summary>
    /// <paramref name="name"/> as a file name for a response of <paramref name="mediaType"/>:
    /// characters no file system accepts (and the path separators of every platform, since the
    /// name may come from a URL) become <c>-</c>, and the media type's extension is added unless
    /// the name already ends with it.
    /// </summary>
    public static string Suggest(string name, string? mediaType)
    {
        var safe = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
            safe.Append(IsUnsafe(c) ? '-' : c);

        string stem = safe.ToString().Trim(' ', '.');
        if (stem.Length == 0)
            stem = "response";

        string extension = ExtensionFor(mediaType);
        return stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? stem : stem + extension;
    }

    // Windows' list is the widest; checking it everywhere keeps a name valid wherever it is copied.
    private static bool IsUnsafe(char c)
        => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c)
            || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0;
}
