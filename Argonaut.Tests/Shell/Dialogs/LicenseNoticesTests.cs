using Argonaut.Shell.Dialogs;

namespace Argonaut.Tests.Shell.Dialogs;

/// <summary>
/// The embedded licence and third-party notices. The generator refuses to write a file that leaves
/// a shipped package uncovered; these pin that the embedded copy is the generated one, that the
/// notices whose licences demand them specifically are present, and that the reader splits
/// sections only on the generator's fences.
/// </summary>
public class LicenseNoticesTests
{
    [Fact]
    public void Load_StartsWithArgonautsOwnMitLicense()
    {
        LicenseNotice first = LicenseNotices.Load()[0];

        Assert.Equal(AppInfo.Name, first.Component);
        Assert.Equal(AppInfo.RepoUrl, first.Homepage);
        Assert.StartsWith("MIT License", first.NoticeText);
    }

    [Theory]
    [InlineData("Avalonia")]
    [InlineData("Inter font")]
    [InlineData("SkiaSharp and HarfBuzzSharp")]
    [InlineData("ANGLE (Windows)")]
    [InlineData(".NET Runtime")]
    [InlineData("Microsoft.IO.RecyclableMemoryStream")]
    [InlineData("MicroCom")]
    [InlineData("Tmds.DBus")]
    [InlineData("Velopack")]
    [InlineData("Unicode Character Database")]
    public void Load_IncludesEveryShippedComponent(string component)
    {
        LicenseNotice notice = Assert.Single(LicenseNotices.Load(), n => n.Component == component);

        Assert.NotNull(notice.Homepage);
        Assert.False(string.IsNullOrWhiteSpace(notice.NoticeText));
    }

    /// <summary>
    /// Notices easy to lose: xxHash (BSD-2) is inside System.IO.Hashing and only named in the
    /// runtime's notices; the Inter font is OFL, not the MIT of the package that embeds it; and
    /// FreeType's licence asks for a credit line in the documentation.
    /// </summary>
    [Theory]
    [InlineData(".NET Runtime", "License notice for xxHash")]
    [InlineData("Inter font", "SIL OPEN FONT LICENSE Version 1.1")]
    [InlineData("SkiaSharp and HarfBuzzSharp", "The FreeType Project")]
    [InlineData("Unicode Character Database", "UNICODE LICENSE V3")]
    public void Load_CarriesNoticesTheLicencesRequire(string component, string expected)
    {
        LicenseNotice notice = Assert.Single(LicenseNotices.Load(), n => n.Component == component);

        Assert.Contains(expected, notice.NoticeText);
    }

    [Fact]
    public void Parse_ReadsHeaderKeysAndBody()
    {
        string rule = new('=', 80);
        string notices =
            $"{rule}\nComponent: Alpha\nHomepage: https://example.com/alpha\nPackages: Alpha 1.0.0\n{rule}\n\n" +
            "Alpha licence\n\n" +
            $"{rule}\nComponent: Beta\nHomepage: https://example.com/beta\n{rule}\n\nBeta licence\n";

        var sections = LicenseNotices.Parse(notices);

        Assert.Collection(sections,
            alpha =>
            {
                Assert.Equal("Alpha", alpha.Component);
                Assert.Equal("https://example.com/alpha", alpha.Homepage);
                Assert.Equal("Alpha 1.0.0", alpha.Packages);
                Assert.Equal("Alpha licence", alpha.NoticeText);
            },
            beta =>
            {
                Assert.Equal("Beta", beta.Component);
                Assert.Null(beta.Packages);
                Assert.Equal("Beta licence", beta.NoticeText);
            });
    }

    /// <summary>Licence texts contain their own rules; only a fence followed by "Component:" opens a section.</summary>
    [Fact]
    public void Parse_KeepsRulesInsideNoticeText()
    {
        string rule = new('=', 80);
        string notices = $"{rule}\nComponent: Alpha\nHomepage: https://example.com\n{rule}\n\nIntro\n{rule}\nLegal Terms\n";

        LicenseNotice alpha = Assert.Single(LicenseNotices.Parse(notices));

        Assert.Equal($"Intro\n{rule}\nLegal Terms", alpha.NoticeText);
    }
}
