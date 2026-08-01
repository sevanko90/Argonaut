using Argonaut.Infrastructure;

namespace Argonaut.Features.NdJson;

/// <summary>
/// Decodes one indexed NDJSON line to text straight from the mapped file bytes.
/// The trailing newline bytes are trimmed from the span before decoding, so only
/// one string is ever allocated per line.
/// </summary>
public static class NdJsonLineReader
{
    public static string ReadLine(MMapFile file, FileLineSpan lineSpan)
    {
        var trimmed = TrimTrailingNewline(file, lineSpan);
        return file.GetUtf8String(trimmed.Offset, trimmed.Length);
    }

    /// <summary>
    /// Decodes a line for display, capped per <see cref="DisplayText"/>. A file with no
    /// newlines at all (a minified JSON document forced into this view) indexes as one line
    /// spanning the whole file, so the uncapped <see cref="ReadLine"/> must never back a
    /// realized row.
    /// </summary>
    public static string ReadDisplayLine(MMapFile file, FileLineSpan lineSpan)
    {
        var trimmed = TrimTrailingNewline(file, lineSpan);
        return DisplayText.Read(file, trimmed.Offset, trimmed.Length, out _);
    }

    /// <summary>
    /// Returns <paramref name="lineSpan"/> with any trailing '\n'/'\r' bytes excluded, so the
    /// range can be handed to something (e.g. a JSON parser) that must not see them.
    /// </summary>
    public static FileLineSpan TrimTrailingNewline(MMapFile file, FileLineSpan lineSpan)
    {
        var span = file.GetSpan(lineSpan.Offset, lineSpan.Length);
        int length = span.Length;
        while (length > 0 && span[length - 1] is (byte)'\n' or (byte)'\r')
            length--;

        return new FileLineSpan(lineSpan.Offset, length);
    }
}
