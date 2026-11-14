using Argonaut.Engine.Settings;

namespace Argonaut.Tests.Engine.Settings;

/// <summary>The key file Linux keeps its sealing key in.</summary>
public sealed class FileSealingKeyStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void ASavedKey_LoadsBack_FromANewStore()
    {
        string path = Path.Combine(folder, "sealing.key");
        byte[] key = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

        Assert.Null(new FileSealingKeyStore(path).LoadKey());
        Assert.True(new FileSealingKeyStore(path).SaveKey(key));

        Assert.Equal(key, new FileSealingKeyStore(path).LoadKey());
    }

    [Fact]
    public void TheKeyFile_IsReadableByTheUserOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = Path.Combine(folder, "sealing.key");
        new FileSealingKeyStore(path).SaveKey(new byte[32]);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void AKeyFileOfTheWrongSize_LoadsAsNoKey()
    {
        string path = Path.Combine(folder, "sealing.key");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Null(new FileSealingKeyStore(path).LoadKey());
    }
}
