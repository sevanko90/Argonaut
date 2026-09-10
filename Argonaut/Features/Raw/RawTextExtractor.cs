using System;
using System.Buffers;
using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Decodes an arbitrary byte range of the document to text, for the clipboard.
///
/// The cap is the point of it. A selection in this viewer is two byte offsets, so selecting a
/// whole 4GB document costs nothing and is a perfectly reasonable thing for a user to do - right
/// up until something asks for it as a string, at which point the honest answer is to refuse
/// rather than to attempt a 4GB allocation (and, decoded to UTF-16, rather more than that).
/// Callers report the refusal; they never get a truncated string that looks like it worked.
///
/// Unlike a row, an extracted range keeps its newlines: it is text the user is copying out, not
/// a line being drawn, so nothing is trimmed and control characters are left as they are rather
/// than substituted for Control Pictures - pasting a Control Picture into another program would
/// be silently wrong.
/// </summary>
public static class RawTextExtractor
{
    /// <summary>Bytes beyond which extraction refuses. 64MB of source is already a ~128MB string.</summary>
    public const long MaxExtractBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Decodes [<paramref name="start"/>, <paramref name="endExclusive"/>) as UTF-8, with
    /// invalid bytes becoming U+FFFD as everywhere else in the raw viewer. Returns false without
    /// allocating when the range is larger than <see cref="MaxExtractBytes"/>.
    /// </summary>
    public static bool TryExtract(IByteSource source, long start, long endExclusive, out string text)
    {
        ArgumentNullException.ThrowIfNull(source);

        long from = Math.Clamp(start, 0, source.Length);
        long to = Math.Clamp(endExclusive, from, source.Length);
        long length = to - from;

        if (length > MaxExtractBytes)
        {
            text = string.Empty;
            return false;
        }

        if (length == 0)
        {
            text = string.Empty;
            return true;
        }

        var contiguous = source.GetContiguousSpan(from, (int)length);
        if (contiguous.Length == length)
        {
            text = Encoding.UTF8.GetString(contiguous);
            return true;
        }

        byte[] gathered = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            int copied = source.CopyTo(from, gathered.AsSpan(0, (int)length));
            text = Encoding.UTF8.GetString(gathered.AsSpan(0, copied));
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(gathered);
        }
    }
}
