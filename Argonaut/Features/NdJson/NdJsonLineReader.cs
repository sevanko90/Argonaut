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
