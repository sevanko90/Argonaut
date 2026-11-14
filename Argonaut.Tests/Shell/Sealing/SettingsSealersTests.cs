using System.Text;
using Argonaut.Shell.Sealing;

namespace Argonaut.Tests.Shell.Sealing;

/// <summary>
/// The platform sealers against the real OS. DPAPI runs on every Windows run. The Keychain test
/// only runs when <c>ARGONAUT_KEYCHAIN_TESTS=1</c>: a CI runner's login keychain may be locked,
/// and an unsigned test host can be shown an access prompt nobody is there to answer.
/// </summary>
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
    public void Keychain_KeepsOneKey_AcrossStores()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ARGONAUT_KEYCHAIN_TESTS") != "1")
            return;

        string account = "test-" + Guid.NewGuid().ToString("N");
        var store = new KeychainSealingKeyStore(KeychainSealingKeyStore.DefaultService, account);
        try
        {
            Assert.Null(store.LoadKey());
            byte[] key = [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7))];
            Assert.True(store.SaveKey(key));

            Assert.Equal(key, new KeychainSealingKeyStore(KeychainSealingKeyStore.DefaultService, account).LoadKey());
        }
        finally
        {
            store.Delete();
        }
    }
}
