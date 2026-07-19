using System.Collections.Generic;
using System.Text;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Csv;

/// <summary>
/// Byte offset/length of one field within a row, relative to the whole mapped file. Building
/// these for row 0 is the "column offset index": a one-time parse of the header/first row that
/// establishes the column count and (when treated as a header) the column labels.
/// </summary>
public readonly record struct CsvFieldSpan(long Offset, int Length);

/// <summary>
/// Splits one CSV/TSV row into fields. Quote handling matches
/// <see cref="FileTypeDetector.DetectFileType"/>'s delimiter counting: a '"' toggles an
/// in-quotes flag for the rest of the row, so a delimiter inside quotes isn't a split point.
/// Quote state never carries across rows - each call starts fresh.
/// </summary>
public static class CsvFieldReader
{
    public static CsvFieldSpan[] SplitToSpans(MMapFile file, FileLineSpan lineSpan, byte delimiter)
    {
        var trimmed = NdJsonLineReader.TrimTrailingNewline(file, lineSpan);
        if (trimmed.Length == 0)
            return [new CsvFieldSpan(trimmed.Offset, 0)];

        var span = file.GetSpan(trimmed.Offset, trimmed.Length);

        var spans = new List<CsvFieldSpan>();
        int fieldStart = 0;
        bool inQuotes = false;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)'"')
                inQuotes = !inQuotes;
            else if (!inQuotes && b == delimiter)
            {
                spans.Add(new CsvFieldSpan(trimmed.Offset + fieldStart, i - fieldStart));
                fieldStart = i + 1;
            }
        }

        spans.Add(new CsvFieldSpan(trimmed.Offset + fieldStart, span.Length - fieldStart));
        return spans.ToArray();
    }

    /// <summary>
    /// Splits and decodes a row to strings, on demand - only ever called for a row the UI is
    /// about to display, mirroring <see cref="NdJsonLineReader.ReadLine"/>'s decode-on-realize
    /// model. A field wrapped in a matching pair of '"' has the quotes stripped and any doubled
    /// '""' unescaped to a literal '"'.
    /// </summary>
    public static string[] ReadFields(MMapFile file, FileLineSpan lineSpan, byte delimiter)
    {
        var spans = SplitToSpans(file, lineSpan, delimiter);
        var fields = new string[spans.Length];
        for (int i = 0; i < spans.Length; i++)
            fields[i] = DecodeField(file, spans[i]);

        return fields;
    }

    private static string DecodeField(MMapFile file, CsvFieldSpan span)
    {
        if (span.Length == 0)
            return string.Empty;

        var bytes = file.GetSpan(span.Offset, span.Length);
        if (bytes.Length >= 2 && bytes[0] == (byte)'"' && bytes[^1] == (byte)'"')
            return Encoding.UTF8.GetString(bytes[1..^1]).Replace("\"\"", "\"");

        return Encoding.UTF8.GetString(bytes);
    }
}
