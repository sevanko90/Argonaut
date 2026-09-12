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

    // How far IsPlausibleFor will scan a first line that never ends. See the CSV/TSV case.
    private const int PreflightScanLimit = 1024 * 1024;

    private static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \t\r\n"u8);

    /// <summary>
    /// Detect whether a file is structurally JSON or NDJson
    /// </summary>
    /// <param name="origin">The document to inspect.</param>
    /// <returns>The detected type of the file</returns>
    /// <remarks>Should handle multi-gb file by scanning bytes rather than trying to parse data</remarks>
    public static FileKind DetectFileType(IByteOrigin origin)
    {
        var mmap = origin.Open();
        try
        {
            return Detect(mmap);
        }
        finally
        {
            mmap.Release();
        }
    }

    private static FileKind Detect(IByteSource mmap)
    {
        long length = mmap.AvailableLength;
        if (length == 0)
            return FileKind.Unidentified;

        // 1. Find first non-whitespace character for future detection.
        long firstCharOffset = FindNonWhitespace(mmap, 0, length);
        if (firstCharOffset < 0)
            return FileKind.Unidentified;

        // 2. JSON Starts with { or [. If it's not that, check for CSV/TSV, or default to unidentified. 
        // fast early-out, before bothering to read for the json/ndjson distinction.
        byte firstChar = mmap.ByteAt(firstCharOffset);
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

        byte secondFirstChar = mmap.ByteAt(secondLineStart);

        // 5. NDJSON rule: first line ends with } and the second line starts with {.
        return lastCharFirstLine == (byte)'}' && secondFirstChar == (byte)'{'
            ? FileKind.Ndjson
            : FileKind.Json;
    }

    /// <summary>
    /// Cheap pre-flight check for whether <paramref name="path"/> is plausibly a
    /// <paramref name="kind"/> file, without running a full background index. Used when the
    /// user forces a view onto a file detection didn't pick, to reject obvious mismatches
    /// (e.g. forcing JSON onto a CSV file) instantly instead of waiting for the indexer to
    /// fail partway through.
    /// </summary>
    /// <param name="kind">The view the user is forcing.</param>
    /// <param name="origin">The document to inspect.</param>
    /// <param name="reason">Set to a human-readable explanation when this returns false.</param>
    public static bool IsPlausibleFor(FileKind kind, IByteOrigin origin, out string reason)
    {
        var mmap = origin.Open();
        try
        {
            return IsPlausible(kind, mmap, out reason);
        }
        finally
        {
            mmap.Release();
        }
    }

    private static bool IsPlausible(FileKind kind, IByteSource mmap, out string reason)
    {
        long length = mmap.AvailableLength;

        switch (kind)
        {
            case FileKind.Unidentified:
                // Raw is the guaranteed-safe fallback - always plausible.
                reason = "";
                return true;

            case FileKind.Json:
            case FileKind.Ndjson:
            {
                long firstCharOffset = FindNonWhitespace(mmap, 0, length);
                if (firstCharOffset < 0)
                {
                    reason = "The file is empty or contains only whitespace.";
                    return false;
                }

                byte firstChar = mmap.ByteAt(firstCharOffset);
                if (firstChar is (byte)'{' or (byte)'[')
                {
                    reason = "";
                    return true;
                }

                reason = $"Expected the first non-whitespace byte to be '{{' or '[', but found '{(char)firstChar}' at byte offset {firstCharOffset}.";
                return false;
            }

            case FileKind.Csv:
            case FileKind.Tsv:
            {
                // Inspect at most a prefix of the first line. This runs on the UI thread before
                // any background indexing starts, and CountUnquotedDelimiter is a byte-at-a-time
                // scan - on a file with no newline at all (a minified JSON document forced into
                // this view) the "first line" is the entire file, so an uncapped scan would
                // freeze the UI for the whole pre-flight. A prefix is enough either way: one
                // delimiter is all this check looks for.
                long firstLineEnd = FindNewline(mmap, 0, Math.Min(length, PreflightScanLimit));
                long firstLineLength = firstLineEnd < 0 ? Math.Min(length, PreflightScanLimit) : firstLineEnd;
                var firstLine = mmap.RequireContiguous(0, checked((int)firstLineLength));

                byte delimiter = kind == FileKind.Csv ? (byte)',' : (byte)'\t';
                if (CountUnquotedDelimiter(firstLine, delimiter) > 0)
                {
                    reason = "";
                    return true;
                }

                reason = firstLineEnd < 0 && length > PreflightScanLimit
                    ? $"The first {PreflightScanLimit / 1024:N0} KB contain no line break and no unquoted '{(char)delimiter}' delimiter."
                    : $"The first line contains no unquoted '{(char)delimiter}' delimiter.";
                return false;
            }

            default:
                reason = $"Unsupported file kind: {kind}.";
                return false;
        }
    }

    /// <summary>
    /// CSV/TSV rule: not JSON/NDJSON, but the first two physical lines have an equal, non-zero
    /// count of unquoted commas (or, failing that, unquoted tabs). Comma is checked first as
    /// the tie-break for the rare file where both counts happen to match.
    /// </summary>
    private static FileKind DetectDelimitedOrUnknown(IByteSource file, long length)
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

        var line1 = file.RequireContiguous(0, checked((int)firstLineEnd));
        var line2 = file.RequireContiguous(secondLineStart, checked((int)(secondLineEnd - secondLineStart)));

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

    // The chunked-scan loops in these three helpers (and in FileOffsetIndex/SearchSession)
    // are deliberately duplicated, not abstracted: they're hot paths, and the indirection an
    // abstraction would add costs more than the ~15 shared lines save.
    private static long FindNonWhitespace(IByteSource file, long start, long end)
    {
        for (long offset = start; offset < end;)
        {
            var chunk = file.GetContiguousSpan(offset, (int)Math.Min(ChunkSize, end - offset));
            if (chunk.IsEmpty)
                break;

            int i = chunk.IndexOfAnyExcept(Whitespace);
            if (i >= 0)
                return offset + i;

            offset += chunk.Length;
        }

        return -1;
    }

    private static long FindNewline(IByteSource file, long start, long end)
    {
        for (long offset = start; offset < end;)
        {
            var chunk = file.GetContiguousSpan(offset, (int)Math.Min(ChunkSize, end - offset));
            if (chunk.IsEmpty)
                break;

            int i = chunk.IndexOf((byte)'\n');
            if (i >= 0)
                return offset + i;

            offset += chunk.Length;
        }

        return -1;
    }

    /// <summary>
    /// Scans backwards, so unlike the forward loops it cannot simply take whatever the source
    /// serves: a short return would leave the tail of the chunk unexamined and the answer would
    /// be the wrong byte. It walks forward within each chunk window instead, which is correct
    /// whatever the source hands back.
    /// </summary>
    private static byte LastNonWhitespaceBefore(IByteSource file, long start, long endExclusive)
    {
        for (long offset = endExclusive; offset > start;)
        {
            long windowStart = offset - Math.Min(ChunkSize, offset - start);
            byte found = 0;
            bool any = false;

            for (long at = windowStart; at < offset;)
            {
                var chunk = file.GetContiguousSpan(at, (int)(offset - at));
                if (chunk.IsEmpty)
                    break;

                int i = chunk.LastIndexOfAnyExcept(Whitespace);
                if (i >= 0)
                {
                    found = chunk[i];
                    any = true;
                }

                at += chunk.Length;
            }

            if (any)
                return found;

            offset = windowStart;
        }

        return 0;
    }
}
