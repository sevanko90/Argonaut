using System.Collections.Generic;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed class NdjsonLine
{
    public long Offset { get; init; }
    public int Length { get; init; }
}

public static class NdjsonIndexer
{
    private const int BufferSize = 64 * 1024;

    public static IReadOnlyList<NdjsonLine> Build(MMapFile file)
    {
        var list = new List<NdjsonLine>();
        long offset = 0;
        long length = file.Length;

        var buffer = new byte[BufferSize];

        while (offset < length)
        {
            int bytesRead = file.Read(offset, buffer);

            int start = 0;
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    long lineStart = offset + start;
                    long lineEnd = offset + i + 1;
                    list.Add(new NdjsonLine
                    {
                        Offset = lineStart,
                        Length = (int)(lineEnd - lineStart)
                    });
                    start = i + 1;
                }
            }

            offset += bytesRead;
        }

        return list;
    }
}
