using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies reads through <see cref="MMapFile"/> are bounded by the real file length
/// (from FileInfo), never the mapping capacity - the OS rounds the mapping up to its
/// allocation granularity, and the trailing zero-padding must never be readable as data
/// (see CLAUDE.md).
/// </summary>
public class MMapFileTests
{
    private static void WithFile(byte[] content, Action<MMapFile> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            assert(file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reading_AfterDispose_ThrowsObjectDisposed_NotAccessViolation()
    {
        // A read after Dispose dereferences released memory. Without the guard this is an
        // uncatchable AccessViolationException that crashes the process (the shell hit this
        // when a virtualizing list enumerated an mmap-backed collection during the content
        // swap that disposed it); the guard makes it a catchable managed failure instead.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello, mapped world"));
            var file = new MMapFile(path);
            file.Dispose();

            Assert.Throws<ObjectDisposedException>(() => file.GetUtf8String(0, 5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Length_ComesFromFileNotMappingCapacity()
    {
        // 100 bytes is far below any platform's allocation granularity, so a capacity-derived
        // length would be wrong here.
        WithFile(new byte[100], file => Assert.Equal(100, file.AvailableLength));
    }

    [Fact]
    public void RequireContiguous_ReturnsExactFileBytes()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello, mapped world");
        WithFile(content, file =>
        {
            Assert.True(file.RequireContiguous(0, content.Length).SequenceEqual(content));
            Assert.True(file.RequireContiguous(7, 6).SequenceEqual("mapped"u8));
        });
    }

    [Fact]
    public void RequireContiguous_RequestPastEndOfFile_Throws()
    {
        WithFile(new byte[100], file =>
        {
            // Both shapes of overrun: too-long from the start, and offset beyond the end.
            // The mapping itself is larger (capacity padding), so these only throw if
            // bounds come from the real file length.
            Assert.Throws<ArgumentOutOfRangeException>(() => file.RequireContiguous(0, 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.RequireContiguous(100, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.RequireContiguous(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.RequireContiguous(0, -1));
        });
    }

    [Fact]
    public void RequireContiguous_ZeroLengthAtEndOfFile_YieldsEmptySpan()
    {
        WithFile(new byte[100], file => Assert.Equal(0, file.RequireContiguous(100, 0).Length));
    }

    [Fact]
    public void EmptyFile_HasZeroLengthAndYieldsEmptySpan()
    {
        WithFile(Array.Empty<byte>(), file =>
        {
            Assert.Equal(0, file.AvailableLength);
            Assert.Equal(0, file.RequireContiguous(0, 0).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => file.RequireContiguous(0, 1));
        });
    }

    [Fact]
    public void RangedConstructor_ReturnsExactSubRangeBytes()
    {
        byte[] content = Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n{\"c\":3}");
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);

            using var middle = new MMapFile(path, 8, 7);
            Assert.Equal(7, middle.AvailableLength);
            Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(middle.RequireContiguous(0, (int)middle.AvailableLength)));

            using var last = new MMapFile(path, 16, 7);
            Assert.Equal("{\"c\":3}", Encoding.UTF8.GetString(last.RequireContiguous(0, (int)last.AvailableLength)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
