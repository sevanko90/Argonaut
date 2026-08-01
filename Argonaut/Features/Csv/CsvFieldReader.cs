using System;
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
    /// <summary>
    /// Display cap on how many fields one row contributes to the grid. A row's field count is
    /// bounded only by its length, and the CSV grid renders cells into a non-virtualizing
    /// StackPanel (both the sticky header and each row) - so a file whose "first line" is the
    /// whole file, as a minified JSON document forced into this view is, would otherwise build
    /// millions of controls per row and never finish laying out. See <see cref="DisplayText"/>.
    /// </summary>
    public const int MaxDisplayFields = 1000;

    /// <param name="maxFields">
    /// Stop after this many fields, treating the rest of the row as one final field. Omit for
    /// the true, uncapped split - what search needs, since a match can land in any column.
    /// </param>
    public static CsvFieldSpan[] SplitToSpans(MMapFile file, FileLineSpan lineSpan, byte delimiter, int maxFields = int.MaxValue)
    {
        var trimmed = NdJsonLineReader.TrimTrailingNewline(file, lineSpan);
        if (trimmed.Length == 0)
            return [new CsvFieldSpan(trimmed.Offset, 0)];

        var span = file.GetSpan(trimmed.Offset, trimmed.Length);

        // Two passes over the (already mapped, no I/O) row bytes instead of a growing
        // List<CsvFieldSpan> + ToArray(): counting first means the result array is
        // allocated exactly once, at its final size, with no intermediate growth copies.
        // Counting stops at the cap so a pathological row costs a bounded array, not a
        // multi-million-element one.
        int fieldCount = 1;
        bool inQuotes = false;
        for (int i = 0; i < span.Length && fieldCount < maxFields; i++)
        {
            byte b = span[i];
            if (b == (byte)'"')
                inQuotes = !inQuotes;
            else if (!inQuotes && b == delimiter)
                fieldCount++;
        }

        var spans = new CsvFieldSpan[fieldCount];
        int fieldStart = 0;
        int fieldIndex = 0;
        inQuotes = false;
        for (int i = 0; i < span.Length && fieldIndex < fieldCount - 1; i++)
        {
            byte b = span[i];
            if (b == (byte)'"')
                inQuotes = !inQuotes;
            else if (!inQuotes && b == delimiter)
            {
                spans[fieldIndex++] = new CsvFieldSpan(trimmed.Offset + fieldStart, i - fieldStart);
                fieldStart = i + 1;
            }
        }

        // Everything left over, which is just the last field when uncapped and the whole
        // remainder of the row when the cap cut the split short. Decoding is capped
        // separately, so an oversized remainder still never becomes a huge string.
        spans[fieldIndex] = new CsvFieldSpan(trimmed.Offset + fieldStart, span.Length - fieldStart);
        return spans;
    }

    /// <summary>
    /// Splits and decodes a row to strings, on demand - only ever called for a row the UI is
    /// about to display, mirroring <see cref="NdJsonLineReader.ReadLine"/>'s decode-on-realize
    /// model. A field wrapped in a matching pair of '"' has the quotes stripped and any doubled
    /// '""' unescaped to a literal '"'.
    /// </summary>
    public static string[] ReadFields(MMapFile file, FileLineSpan lineSpan, byte delimiter)
    {
        var spans = SplitToSpans(file, lineSpan, delimiter, MaxDisplayFields);
        var fields = new string[spans.Length];
        for (int i = 0; i < spans.Length; i++)
            fields[i] = DecodeField(file, spans[i]);

        return fields;
    }

    private static string DecodeField(MMapFile file, CsvFieldSpan span)
    {
        if (span.Length == 0)
            return string.Empty;

        // Quote stripping/unescaping only applies to a field short enough to be decoded whole.
        // Past the cap the field is truncated mid-content anyway, so its closing quote (if any)
        // is not in the decoded text and there is no matching pair to strip.
        if (span.Length > DisplayText.MaxLength)
            return DisplayText.Read(file, span.Offset, span.Length, out _);

        var bytes = file.GetSpan(span.Offset, span.Length);
        if (bytes.Length >= 2 && bytes[0] == (byte)'"' && bytes[^1] == (byte)'"')
            return Encoding.UTF8.GetString(bytes[1..^1]).Replace("\"\"", "\"");

        return Encoding.UTF8.GetString(bytes);
    }
}
