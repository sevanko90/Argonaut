using System.Text;
using Argonaut.Engine.Settings;
using Argonaut.Shell.Sealing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Shell.Sealing;

/// <summary>The platform sealers against the real OS: DPAPI on Windows, the key file elsewhere.</summary>
public sealed class SettingsSealersTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("""{"location":"https://example.com/?key=SECRET"}""");

    [Fact]
    public void Dpapi_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sealer = new DpapiSettingsSealer();
        string sealedText = sealer.Seal(Content)!;

        Assert.Equal(Content, sealer.Open(sealedText));
        Assert.DoesNotContain("SECRET", sealedText);
        Assert.Null(sealer.Open("aesgcm1:AAAA"));
    }

    [Fact]
    public void OffWindows_TheKeyFileSeals_AndANewSealerOpensIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        string folder = Path.Combine(Path.GetTempPath(), "argonaut-sealers-" + Guid.NewGuid().ToString("N"));
        string keyFile = Path.Combine(folder, "sealing.key");
        try
        {
            string sealedText = SettingsSealers.ForThisPlatform(keyFile).Seal(Content)!;

            Assert.StartsWith(AesGcmSettingsSealer.Prefix, sealedText);
            Assert.DoesNotContain("SECRET", sealedText);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
            Assert.Equal(Content, SettingsSealers.ForThisPlatform(keyFile).Open(sealedText));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void OffWindows_ABlockSealedUnderAKeyThatIsGone_OpensAsNothing_AndSealingCarriesOn()
    {
        if (OperatingSystem.IsWindows())
            return;

        // A 2.3.1-2.4.0 macOS install sealed under a Keychain key this build cannot read.
        string folder = Path.Combine(Path.GetTempPath(), "argonaut-sealers-" + Guid.NewGuid().ToString("N"));
        string keyFile = Path.Combine(folder, "sealing.key");
        try
        {
            var lostKey = new MemorySealingKeyStore();
            string orphaned = new AesGcmSettingsSealer(lostKey).Seal(Content)!;

            var sealer = SettingsSealers.ForThisPlatform(keyFile);
            Assert.Null(sealer.Open(orphaned));

            string resealed = sealer.Seal(Content)!;
            Assert.Equal(Content, SettingsSealers.ForThisPlatform(keyFile).Open(resealed));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
