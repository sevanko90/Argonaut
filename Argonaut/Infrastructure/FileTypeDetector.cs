using System;
using System.Buffers;

namespace Argonaut.Infrastructure;

public static class FileTypeDetector
{
    public enum FileKind
    {
        NotJson,
        Json,
        Ndjson
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
            return FileKind.NotJson;

        // 1. First non-whitespace byte must open a JSON container.
        long firstCharOffset = FindNonWhitespace(mmap, 0, length);
        if (firstCharOffset < 0)
            return FileKind.NotJson;

        byte firstChar = mmap.GetSpan(firstCharOffset, 1)[0];
        if (firstChar is not (byte)'{' and not (byte)'[')
            return FileKind.NotJson;

        // 2. Find the end of the first line and its last non-whitespace character.
        long firstLineEnd = FindNewline(mmap, firstCharOffset, length);
        if (firstLineEnd < 0)
            return FileKind.Json; // single-line file

        byte lastCharFirstLine = LastNonWhitespaceBefore(mmap, firstCharOffset, firstLineEnd);

        // 3. Find the first character of the next non-empty line.
        long secondLineStart = FindNonWhitespace(mmap, firstLineEnd + 1, length);
        if (secondLineStart < 0)
            return FileKind.Json;

        byte secondFirstChar = mmap.GetSpan(secondLineStart, 1)[0];

        // 4. NDJSON rule: first line ends with } and the second line starts with {.
        return lastCharFirstLine == (byte)'}' && secondFirstChar == (byte)'{'
            ? FileKind.Ndjson
            : FileKind.Json;
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
