using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Argonaut.Infrastructure;

/// <summary>
/// Read-only memory-mapped view of a file, exposing zero-copy spans over the mapped bytes.
///
/// <see cref="Length"/> always comes from <see cref="FileInfo"/>, never from the accessor's
/// capacity: the OS rounds the mapping up to its allocation granularity, and the trailing
/// zero-padding must never be exposed as data (see CLAUDE.md). <see cref="GetSpan"/> bounds
/// every request against the real file length for the same reason.
/// </summary>
public sealed unsafe class MMapFile : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly byte* _ptr;
    private bool disposed;

    public long Length { get; }

    public MMapFile(string path)
    {
        Length = new FileInfo(path).Length;
        if (Length == 0)
            return; // an empty file can't be mapped; GetSpan can only ever yield an empty span

        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _ptr = ptr + _accessor.PointerOffset;
    }

    /// <summary>
    /// Maps just the byte range [offset, offset + length) of the file at <paramref name="path"/>,
    /// as its own independent OS-level mapping - fully decoupled from any other <see cref="MMapFile"/>
    /// open over the same path. Used to view a sub-document (e.g. one NDJSON line) through the
    /// same zero-copy machinery as a whole file, without inheriting the parent file's absolute
    /// offsets.
    /// </summary>
    public MMapFile(string path, long offset, long length)
    {
        Length = length;
        if (Length == 0)
            return; // an empty range can't be mapped; GetSpan can only ever yield an empty span

        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _ptr = ptr + _accessor.PointerOffset;
    }

    /// <summary>
    /// Returns a zero-copy view of the file bytes [offset, offset + length).
    /// The span is only valid until this <see cref="MMapFile"/> is disposed.
    /// </summary>
    public ReadOnlySpan<byte> GetSpan(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > Length)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Requested range [{offset}, {offset + length}) extends past the end of the file ({Length} bytes).");

        return new ReadOnlySpan<byte>(_ptr + offset, length);
    }

    /// <summary>
    /// Decodes the file bytes [offset, offset + length) as UTF-8. The one place the
    /// "decode text on demand from an (offset, length) span" idiom lives, so every reader
    /// goes through <see cref="GetSpan"/>'s real-length bounds check (see CLAUDE.md).
    /// </summary>
    public string GetUtf8String(long offset, int length)
        => Encoding.UTF8.GetString(GetSpan(offset, length));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}
