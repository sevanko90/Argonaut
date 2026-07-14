using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace JsonViewerCore.Infrastructure;

public sealed class MMapFile : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public long Length => _accessor.Capacity;

    public MMapFile(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public int Read(long offset, byte[] buffer)
        => _accessor.ReadArray(offset, buffer, 0, buffer.Length);

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}