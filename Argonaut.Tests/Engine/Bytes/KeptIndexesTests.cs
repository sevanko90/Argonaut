using Argonaut.Engine.Bytes;

namespace Argonaut.Tests.Engine.Bytes;

/// <summary>
/// The per-origin store of finished indexes: an entry comes back only at the version it was built
/// at, and keeping one at a new version drops everything that described the old bytes.
/// </summary>
public class KeptIndexesTests
{
    private static readonly ByteOriginVersion Before = new(100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private static readonly ByteOriginVersion After = new(100, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void TryGet_AtTheVersionItWasKeptAt_ReturnsIt()
    {
        var kept = new KeptIndexes();
        var index = new object();
        kept.Keep("rows", Before, index);

        Assert.True(kept.TryGet<object>("rows", Before, out var found));
        Assert.Same(index, found);
    }

    [Fact]
    public void TryGet_AtAnotherVersion_MissesAndForgetsIt()
    {
        var kept = new KeptIndexes();
        var index = new object();
        kept.Keep("rows", Before, index);

        Assert.False(kept.TryGet<object>("rows", After, out _));
        Assert.False(kept.TryGet<object>("rows", Before, out _));
    }

    [Fact]
    public void Keep_AtANewVersion_DropsEntriesForTheOldOne()
    {
        var kept = new KeptIndexes();
        kept.Keep("narrow", Before, new object());
        kept.Keep("wide", After, new object());

        Assert.False(kept.TryGet<object>("narrow", Before, out _));
        Assert.True(kept.TryGet<object>("wide", After, out _));
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var kept = new KeptIndexes();
        kept.Keep("rows", Before, new object());

        kept.Clear();

        Assert.False(kept.TryGet<object>("rows", Before, out _));
    }

    [Fact]
    public void Version_OfAFile_ChangesWhenItIsWritten()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var origin = new FileByteOrigin(path);
            var first = ByteOriginVersion.Of(origin);

            File.WriteAllText(path, "xyz"); // same length, different bytes
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            Assert.NotNull(first);
            Assert.NotEqual(first, ByteOriginVersion.Of(origin));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Version_OfAPaste_IsItsLength()
    {
        var origin = new MemoryByteOrigin(new byte[42], "Pasted text");

        Assert.Equal(new ByteOriginVersion(42, null), ByteOriginVersion.Of(origin));
    }
}
