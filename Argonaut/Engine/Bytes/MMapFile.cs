using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// Read-only memory-mapped view of a file, exposing zero-copy spans over the mapped bytes.
///
/// <see cref="Length"/> always comes from <see cref="FileInfo"/>, never from the accessor's
/// capacity: the OS rounds the mapping up to its allocation granularity, and the trailing
/// zero-padding must never be exposed as data (see CLAUDE.md). <see cref="GetContiguousSpan"/>
/// bounds every request against the real file length for the same reason.
/// </summary>
public sealed unsafe class MMapFile : IByteSource, IDisposable
{
    private readonly MemoryMappedFile? mmf;
    private readonly MemoryMappedViewAccessor? accessor;
    private readonly byte* ptr;
    private bool disposed;

    public long AvailableLength { get; }

    public MMapFile(string path)
    {
        AvailableLength = new FileInfo(path).Length;
        if (AvailableLength == 0)
            return; // an empty file can't be mapped; GetContiguousSpan can only ever yield an empty span

        this.mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        this.accessor = this.mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        this.accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        this.ptr = ptr + this.accessor.PointerOffset;
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
        AvailableLength = length;
        if (AvailableLength == 0)
            return; // an empty range can't be mapped; GetContiguousSpan can only ever yield an empty span

        this.mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        this.accessor = this.mmf.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        this.accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        this.ptr = ptr + this.accessor.PointerOffset;
    }

    /// <summary>
    /// See <see cref="IByteSource.GetContiguousSpan"/>. A mapping is one buffer, so this only
    /// ever truncates at end of file - it never splits a request the way a piece table does,
    /// which is why <see cref="ByteSourceReading.RequireContiguous"/> over a mapping is always
    /// the zero-copy path.
    /// </summary>
    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
    {
        // A read after Dispose dereferences released memory - a native use-after-free that
        // surfaces as an uncatchable AccessViolationException. Fail as a catchable managed
        // exception instead, so an ordering mistake (e.g. a view enumerating an mmap-backed
        // collection after its mapping was disposed) is diagnosable rather than a hard crash.
        ObjectDisposedException.ThrowIf(disposed, this);

        if (offset < 0 || offset >= AvailableLength || maxLength <= 0)
            return ReadOnlySpan<byte>.Empty;

        return new ReadOnlySpan<byte>(this.ptr + offset, (int)Math.Min(maxLength, AvailableLength - offset));
    }

    /// <summary>
    /// See <see cref="IByteSource.Prefetch"/>. A scan of a cold file through a mapping is
    /// otherwise one page fault at a time, each waiting for its own read: measured on an Apple
    /// SSD that sustains 6.5 GiB/s, a sequential fault-driven scan got 1.7 GiB/s, and the same
    /// scan hinting 64 MB ahead got 4.6 GiB/s. <c>madvise(MADV_WILLNEED)</c> on macOS and Linux,
    /// <c>PrefetchVirtualMemory</c> on Windows; both start the reads and return. Failure is
    /// ignored - it is only a hint, and the scan reads the bytes either way.
    /// </summary>
    public void Prefetch(long offset, long length)
    {
        if (!prefetchAvailable || disposed || this.ptr == null || offset < 0 || offset >= AvailableLength || length <= 0)
            return;

        length = Math.Min(length, AvailableLength - offset);

        // The calls want a page-aligned start; widen the range down to the page holding it.
        long pageSize = Environment.SystemPageSize;
        long address = (long)(this.ptr + offset);
        long aligned = address & ~(pageSize - 1);
        var size = (nuint)(length + (address - aligned));

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var range = new MemoryRangeEntry { VirtualAddress = (nint)aligned, NumberOfBytes = size };
                _ = PrefetchVirtualMemory(GetCurrentProcess(), 1, &range, 0);
            }
            else
            {
                _ = madvise((nint)aligned, size, MadviseWillNeed);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A platform where the call cannot be bound (a Linux without a "libc" the loader can
            // name, a Windows before 8): scans run unhinted rather than retrying on every window.
            prefetchAvailable = false;
        }
    }

    /// <summary>False once the platform has turned out not to have the prefetch call.</summary>
    private static bool prefetchAvailable = true;

    /// <summary><c>MADV_WILLNEED</c>, the same value on macOS and Linux.</summary>
    private const int MadviseWillNeed = 3;

    [DllImport("libc")]
    private static extern int madvise(nint address, nuint length, int advice);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRangeEntry
    {
        public nint VirtualAddress;
        public nuint NumberOfBytes;
    }

    [DllImport("kernel32")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32")]
    private static extern int PrefetchVirtualMemory(nint process, nuint count, MemoryRangeEntry* ranges, uint flags);

    /// <summary>See <see cref="IByteSource.CopyTo"/>. One mapping, so this is a single copy.</summary>
    public int CopyTo(long offset, Span<byte> destination)
    {
        var span = GetContiguousSpan(offset, destination.Length);
        span.CopyTo(destination);
        return span.Length;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        this.accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        this.accessor?.Dispose();
        this.mmf?.Dispose();
    }
}
