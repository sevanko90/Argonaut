using System;
using System.IO;

namespace JsonViewerCore.Infrastructure;

public static class FileTypeDetector
{
    public enum FileKind
    {
        NotJson,
        Json,
        Ndjson
    }

    static bool IsWhitespace(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>
    /// Detect whether a file is structurally JSON or NDJson
    /// </summary>
    /// <param name="path">Path to file</param>
    /// <returns>The detected type of the file</returns>
    /// <remarks>Should handle multi-gb file by scanning bytes rather than trying to parse data</remarks>
    public static FileKind DetectFileType(string path)
    {
        using var mmap = new MMapFile(path);

        const int ChunkSize = 64 * 1024;
        long length = mmap.Length;
        if (length == 0)
            return FileKind.NotJson;

        byte[] buf = new byte[ChunkSize];
        long offset = 0;

        // -------------------------------
        // 1. Find first non-whitespace byte
        // -------------------------------
        byte firstChar = 0;
        bool foundFirst = false;

        while (offset < length)
        {
            int read = mmap.Read(offset, buf);
            if (read == 0) break;

            for (int i = 0; i < read; i++)
            {
                byte b = buf[i];
                if (!IsWhitespace(b))
                {
                    firstChar = b;
                    offset += i;
                    foundFirst = true;
                    break;
                }
            }

            if (foundFirst)
                break;

            offset += read;
        }

        if (!foundFirst)
            return FileKind.NotJson;

        if (firstChar is not (byte)'{' and not (byte)'[')
            return FileKind.NotJson;

        // -------------------------------
        // 2. Scan first line forward
        //    Track last non-whitespace char
        // -------------------------------
        byte lastCharFirstLine = 0;
        long firstLineEnd = -1;

        long scan = offset;

        while (scan < length)
        {
            int read = mmap.Read(scan, buf);
            if (read == 0) break;

            for (int i = 0; i < read; i++)
            {
                byte b = buf[i];

                if (b == (byte)'\n')
                {
                    firstLineEnd = scan + i;
                    scan += i + 1;
                    break;
                }

                if (!IsWhitespace(b))
                    lastCharFirstLine = b;
            }

            if (firstLineEnd != -1)
                break;

            scan += read;
        }

        if (firstLineEnd == -1)
            return FileKind.Json; // single-line file

        offset = firstLineEnd + 1;

        // -------------------------------
        // 3. Find start of second non-empty line
        // -------------------------------
        long secondLineStart = -1;
        byte secondFirstChar = 0;

        while (offset < length)
        {
            int read = mmap.Read(offset, buf);
            if (read == 0) break;

            for (int i = 0; i < read; i++)
            {
                byte b = buf[i];
                if (!IsWhitespace(b))
                {
                    secondLineStart = offset + i;
                    secondFirstChar = b;
                    break;
                }
            }

            if (secondLineStart != -1)
                break;

            offset += read;
        }

        if (secondLineStart == -1)
            return FileKind.Json;

        // -------------------------------
        // 4. NDJSON rule
        // It's NDJson if the first line ends with } and the second line starts with {
        // -------------------------------
        if (lastCharFirstLine == (byte)'}' && secondFirstChar == (byte)'{')
            return FileKind.Ndjson;

        //  fallback as regular json
        return FileKind.Json;
    }

}
