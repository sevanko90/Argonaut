using System;
using System.Collections.Generic;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed class NdJsonOffsetIndex
{
    private const int BufferSize = 256 * 1024;

    private readonly IReadOnlyList<long> _lineStarts;

    private NdJsonOffsetIndex(IReadOnlyList<long> lineStarts)
    {
        _lineStarts = lineStarts;
    }

    public int LineCount => _lineStarts.Count;

    public long GetOffset(int lineIndex) => _lineStarts[lineIndex];

    public long GetLength(int lineIndex, long fileLength)
    {
        var start = _lineStarts[lineIndex];
        var end = lineIndex + 1 < _lineStarts.Count ? _lineStarts[lineIndex + 1] : fileLength;
        return end - start;
    }

    public static NdJsonOffsetIndex Build(MMapFile file)
    {
        var lineStarts = new List<long>();

        long length = file.Length;
        if (length == 0)
            return new NdJsonOffsetIndex(lineStarts);

        lineStarts.Add(0);

        var buffer = new byte[BufferSize];
        long offset = 0;

        while (offset < length)
        {
            int bytesRead = file.Read(offset, buffer);
            if (bytesRead == 0)
                break;

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;

                long nextLineStart = offset + i + 1;
                if (nextLineStart < length)
                    lineStarts.Add(nextLineStart);
            }

            offset += bytesRead;
        }

        return new NdJsonOffsetIndex(lineStarts);
    }
}
