using System;
using System.Buffers;

namespace Argonaut.Infrastructure;

public static class FileTypeDetector
{
    public enum FileKind
    {
        /// <summary>
        /// Used to signify that we should auto-detect the file type.
        /// </summary>
        Unknown,

        /// <summary>
        /// File type cannot be identified after auto-detection.
        /// </summary>
        Unidentified,
        /// <summary>
        /// JSON File.
        /// </summary>
        Json,
        /// <summary>
        /// NDJson file.
        /// </summary>
        Ndjson,
        /// <summary>
        /// CSV file.
        /// </summary>
        Csv,
        /// <summary>
        /// TSV file.
        /// </summary>
        Tsv,
    }

    private const int ChunkSize = 64 * 1024;

    private static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \t\r\n"u8);

    /// <summary>
    /// Detect whether a file is structurally JSON or NDJson
    /// </summary>
    /// <param name="path">Path to file</param>
    /// <returns>The detected type of the file</returns>
    /// <remarks>Should handle multi-gb file by scanning bytes rather than trying to parse data</remarks>
    public static FileKind DetectFileType(string path)
    {
        using var mmap = new MMapFile(path);

        long length = mmap.Length;
        if (length == 0)
            return FileKind.Unidentified;

        // 1. Find first non-whitespace character for future detection.
        long firstCharOffset = FindNonWhitespace(mmap, 0, length);
        if (firstCharOffset < 0)
            return FileKind.Unidentified;

        // 2. JSON Starts with { or [. If it's not that, check for CSV/TSV, or default to unidentified. 
        // fast early-out, before bothering to read for the json/ndjson distinction.
        byte firstChar = mmap.GetSpan(firstCharOffset, 1)[0];
        if (firstChar is not (byte)'{' and not (byte)'[')
            return DetectDelimitedOrUnknown(mmap, length);

        // 3. Probably JSON, but could be NDJSON. Read second line to check
        long firstLineEnd = FindNewline(mmap, firstCharOffset, length);
        if (firstLineEnd < 0)
            return FileKind.Json; // single-line file

        byte lastCharFirstLine = LastNonWhitespaceBefore(mmap, firstCharOffset, firstLineEnd);

        // 4. Find the first character of the next non-empty line.
        long secondLineStart = FindNonWhitespace(mmap, firstLineEnd + 1, length);
        if (secondLineStart < 0)
            return FileKind.Json;

        byte secondFirstChar = mmap.GetSpan(secondLineStart, 1)[0];

        // 5. NDJSON rule: first line ends with } and the second line starts with {.
        return lastCharFirstLine == (byte)'}' && secondFirstChar == (byte)'{'
            ? FileKind.Ndjson
            : FileKind.Json;
    }

    /// <summary>
    /// CSV/TSV rule: not JSON/NDJSON, but the first two physical lines have an equal, non-zero
    /// count of unquoted commas (or, failing that, unquoted tabs). Comma is checked first as
    /// the tie-break for the rare file where both counts happen to match.
    /// </summary>
    private static FileKind DetectDelimitedOrUnknown(MMapFile file, long length)
    {
        long firstLineEnd = FindNewline(file, 0, length);
        if (firstLineEnd < 0)
            return FileKind.Unidentified; // single-line file: nothing to compare against

        long secondLineStart = firstLineEnd + 1;
        if (secondLineStart >= length)
            return FileKind.Unidentified;

        long secondLineEnd = FindNewline(file, secondLineStart, length);
        if (secondLineEnd < 0)
            secondLineEnd = length;

        var line1 = file.GetSpan(0, checked((int)firstLineEnd));
        var line2 = file.GetSpan(secondLineStart, checked((int)(secondLineEnd - secondLineStart)));

        int commas1 = CountUnquotedDelimiter(line1, (byte)',');
        int commas2 = CountUnquotedDelimiter(line2, (byte)',');
        if (commas1 > 0 && commas1 == commas2)
            return FileKind.Csv;

        int tabs1 = CountUnquotedDelimiter(line1, (byte)'\t');
        int tabs2 = CountUnquotedDelimiter(line2, (byte)'\t');
        if (tabs1 > 0 && tabs1 == tabs2)
            return FileKind.Tsv;

        return FileKind.Unidentified;
    }

    /// <summary>
    /// Counts occurrences of <paramref name="delimiter"/> in <paramref name="line"/>, ignoring
    /// any that fall inside a quoted span. Quote state resets at the start of every call (i.e.
    /// per line) - an unterminated quote doesn't carry over to the next line.
    /// </summary>
    private static int CountUnquotedDelimiter(ReadOnlySpan<byte> line, byte delimiter)
    {
        int count = 0;
        bool inQuotes = false;
        foreach (byte b in line)
        {
            if (b == (byte)'"')
                inQuotes = !inQuotes;
            else if (!inQuotes && b == delimiter)
                count++;
        }

        return count;
    }

    // The chunked-scan loops in these three helpers (and in FileOffsetIndex/FileSearchSession)
    // are deliberately duplicated, not abstracted: they're hot paths, and the indirection an
    // abstraction would add costs more than the ~15 shared lines save.
    private static long FindNonWhitespace(MMapFile file, long start, long end)
    {
        for (long offset = start; offset < end;)
        {
            int size = (int)Math.Min(ChunkSize, end - offset);
            int i = file.GetSpan(offset, size).IndexOfAnyExcept(Whitespace);
            if (i >= 0)
                return offset + i;

            offset += size;
        }

        return -1;
    }

    private static long FindNewline(MMapFile file, long start, long end)
    {
        for (long offset = start; offset < end;)
        {
            int size = (int)Math.Min(ChunkSize, end - offset);
            int i = file.GetSpan(offset, size).IndexOf((byte)'\n');
            if (i >= 0)
                return offset + i;

            offset += size;
        }

        return -1;
    }

    private static byte LastNonWhitespaceBefore(MMapFile file, long start, long endExclusive)
    {
        for (long offset = endExclusive; offset > start;)
        {
            int size = (int)Math.Min(ChunkSize, offset - start);
            var span = file.GetSpan(offset - size, size);
            int i = span.LastIndexOfAnyExcept(Whitespace);
            if (i >= 0)
                return span[i];

            offset -= size;
        }

        return 0;
    }
}
