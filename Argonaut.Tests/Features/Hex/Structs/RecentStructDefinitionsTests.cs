using Argonaut.Engine.Settings;
using Argonaut.Features.Hex.Structs;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class RecentStructDefinitionsTests
{
    [Fact]
    public void TheMostRecentIsFirst_EachOnce_AndTheListIsCapped()
    {
        var recents = new RecentStructDefinitions();
        for (int i = 0; i < RecentStructDefinitions.MaxEntries + 2; i++)
            recents.Remember($"struct S{i} {{ int a; }};");
        recents.Remember("struct S3 { int a; };");

        Assert.Equal(RecentStructDefinitions.MaxEntries, recents.Entries.Count);
        Assert.Equal("struct S3 { int a; };", recents.Entries[0]);
        Assert.Single(recents.Entries, e => e == "struct S3 { int a; };");
    }

    [Fact]
    public void BlankAndOverlongText_IsNotRemembered()
    {
        var recents = new RecentStructDefinitions();
        recents.Remember("   ");
        recents.Remember(new string('x', RecentStructDefinitions.MaxLength + 1));
        Assert.Empty(recents.Entries);
    }

    [Fact]
    public void TheList_IsSealed_AndOpensAgainNextTime_NeverWrittenInTheClear()
    {
        var sealer = new AesGcmSettingsSealer(new MemorySealingKeyStore());
        var block = new RecentStructDefinitionsBlock();
        new RecentStructDefinitions(block, sealer).Remember("struct Secret { int key; };");

        Assert.DoesNotContain("Secret", block.Sealed);
        Assert.Equal(["struct Secret { int key; };"], new RecentStructDefinitions(block, sealer).Entries);
    }
}
