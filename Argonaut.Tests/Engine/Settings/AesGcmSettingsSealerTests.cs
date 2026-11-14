using System.Text;
using Argonaut.Engine.Settings;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Settings;

/// <summary>Sealing a settings block: what goes in comes out, and anything that is not exactly
/// what this key sealed opens as nothing rather than as an error.</summary>
public sealed class AesGcmSettingsSealerTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("""{"entries":[{"location":"https://api.example.com/x?key=SECRET"}]}""");

    [Fact]
    public void WhatIsSealed_OpensAgain_AndIsNotReadableSealed()
    {
        var sealer = new AesGcmSettingsSealer(new MemorySealingKeyStore());

        string sealedText = sealer.Seal(Content)!;

        Assert.Equal(Content, sealer.Open(sealedText));
        Assert.DoesNotContain("SECRET", sealedText);
        Assert.DoesNotContain("example", sealedText);
    }

    [Fact]
    public void SealingTheSameContentTwice_GivesDifferentText()
    {
        var sealer = new AesGcmSettingsSealer(new MemorySealingKeyStore());

        Assert.NotEqual(sealer.Seal(Content), sealer.Seal(Content));
    }

    [Fact]
    public void TheKeyIsCreatedOnce_AndReusedByLaterSealers()
    {
        var keys = new MemorySealingKeyStore();
        string sealedText = new AesGcmSettingsSealer(keys).Seal(Content)!;

        var reopened = new AesGcmSettingsSealer(keys);

        Assert.Equal(Content, reopened.Open(sealedText));
        reopened.Seal(Content);
        Assert.Equal(1, keys.Saves);
    }

    [Fact]
    public void AnotherKey_OpensNothing()
    {
        string sealedText = new AesGcmSettingsSealer(new MemorySealingKeyStore()).Seal(Content)!;

        Assert.Null(new AesGcmSettingsSealer(new MemorySealingKeyStore()).Open(sealedText));
    }

    [Fact]
    public void TamperedOrForeignText_OpensNothing()
    {
        var sealer = new AesGcmSettingsSealer(new MemorySealingKeyStore());
        string sealedText = sealer.Seal(Content)!;
        char[] chars = sealedText.ToCharArray();
        int last = chars.Length - 2;
        chars[last] = chars[last] == 'A' ? 'B' : 'A';

        Assert.Null(sealer.Open(new string(chars)));
        Assert.Null(sealer.Open(""));
        Assert.Null(sealer.Open("not sealed"));
        Assert.Null(sealer.Open(AesGcmSettingsSealer.Prefix + "!!!not base64"));
        Assert.Null(sealer.Open(AesGcmSettingsSealer.Prefix + Convert.ToBase64String([1, 2, 3])));
    }

    [Fact]
    public void AStoreThatCannotKeepTheKey_SealsNothing()
    {
        var sealer = new AesGcmSettingsSealer(new MemorySealingKeyStore { RefuseSaves = true });

        Assert.Null(sealer.Seal(Content));
    }

    [Fact]
    public void WithNoKeyYet_OpeningCreatesNone()
    {
        var keys = new MemorySealingKeyStore();

        Assert.Null(new AesGcmSettingsSealer(keys).Open(AesGcmSettingsSealer.Prefix + Convert.ToBase64String(new byte[40])));
        Assert.Null(keys.Key);
    }
}
