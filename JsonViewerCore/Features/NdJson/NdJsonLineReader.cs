using System.Text;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

/// <summary>
/// Decodes one indexed NDJSON line to text straight from the mapped file bytes.
/// The trailing newline bytes are trimmed from the span before decoding, so only
/// one string is ever allocated per line.
/// </summary>
public static class NdJsonLineReader
{
    public static string ReadLine(MMapFile file, FileLineSpan lineSpan)
    {
        var span = file.GetSpan(lineSpan.Offset, lineSpan.Length);
        while (span.Length > 0 && span[^1] is (byte)'\n' or (byte)'\r')
            span = span[..^1];

        return Encoding.UTF8.GetString(span);
    }
}
