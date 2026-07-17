using System.Text;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Tests;

/// <summary>
/// Verifies <see cref="MMapFile.GetSpan"/> bounds requests against the real file length
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
    public void Length_ComesFromFileNotMappingCapacity()
    {
        // 100 bytes is far below any platform's allocation granularity, so a capacity-derived
        // length would be wrong here.
        WithFile(new byte[100], file => Assert.Equal(100, file.Length));
    }

    [Fact]
    public void GetSpan_ReturnsExactFileBytes()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello, mapped world");
        WithFile(content, file =>
        {
            Assert.True(file.GetSpan(0, content.Length).SequenceEqual(content));
            Assert.True(file.GetSpan(7, 6).SequenceEqual("mapped"u8));
        });
    }

    [Fact]
    public void GetSpan_RequestPastEndOfFile_Throws()
    {
        WithFile(new byte[100], file =>
        {
            // Both shapes of overrun: too-long from the start, and offset beyond the end.
            // The mapping itself is larger (capacity padding), so these only throw if
            // bounds come from the real file length.
            Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSpan(0, 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSpan(100, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSpan(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSpan(0, -1));
        });
    }

    [Fact]
    public void GetSpan_AtEndOfFile_YieldsEmptySpan()
    {
        WithFile(new byte[100], file => Assert.Equal(0, file.GetSpan(100, 0).Length));
    }

    [Fact]
    public void EmptyFile_HasZeroLengthAndYieldsEmptySpan()
    {
        WithFile(Array.Empty<byte>(), file =>
        {
            Assert.Equal(0, file.Length);
            Assert.Equal(0, file.GetSpan(0, 0).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSpan(0, 1));
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
            Assert.Equal(7, middle.Length);
            Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(middle.GetSpan(0, (int)middle.Length)));

            using var last = new MMapFile(path, 16, 7);
            Assert.Equal("{\"c\":3}", Encoding.UTF8.GetString(last.GetSpan(0, (int)last.Length)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
