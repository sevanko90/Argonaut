using Argonaut.Engine.Bytes;
using Argonaut.Engine.Text;

namespace Argonaut.Engine.Indexing.Lines;

/// <summary>
/// Decodes one indexed line (NDJSON, CSV) to text straight from the mapped file bytes.
/// The trailing newline bytes are trimmed from the span before decoding, so only
/// one string is ever allocated per line.
/// </summary>
public static class LineReader
{
    public static string ReadLine(IByteSource file, FileLineSpan lineSpan)
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
    public static string ReadDisplayLine(IByteSource file, FileLineSpan lineSpan)
    {
        var trimmed = TrimTrailingNewline(file, lineSpan);
        return DisplayText.Read(file, trimmed.Offset, trimmed.Length, out _);
    }

    /// <summary>
    /// Returns <paramref name="lineSpan"/> with any trailing '\n'/'\r' bytes excluded, so the
    /// range can be handed to something (e.g. a JSON parser) that must not see them.
    ///
    /// The newline is looked at a byte at a time rather than read with the line: in a selection
    /// of lines it may be one the view adds after a stretch of the file that has none of its own,
    /// which is not beside the line's bytes in memory. The line without it always is.
    /// </summary>
    public static FileLineSpan TrimTrailingNewline(IByteSource file, FileLineSpan lineSpan)
    {
        int length = lineSpan.Length;
        while (length > 0 && file.ByteAt(lineSpan.Offset + length - 1) is (byte)'\n' or (byte)'\r')
            length--;

        return new FileLineSpan(lineSpan.Offset, length);
    }
}
