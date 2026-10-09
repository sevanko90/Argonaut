using Argonaut.Ui.Documents;

namespace Argonaut.Tests.Ui.Documents;

/// <summary>
/// The status line says where a document is and what the view found; the file's name is the
/// title bar's, so it is not repeated here.
/// </summary>
public class DocumentStatusLineTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Compose_NamesTheFolderNotTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "data", "big.json");

        string line = DocumentStatusLine.Compose(path, "25.1 MB");

        Assert.EndsWith(" · 25.1 MB", line);
        Assert.DoesNotContain("big.json", line);
        Assert.StartsWith(DocumentStatusLine.Location(path), line);
    }

    [Fact]
    public void Location_ShortensTheHomeFolder_OutsideWindows()
    {
        string path = Path.Combine(Home, "testData", "big.json");

        string location = DocumentStatusLine.Location(path);

        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(Home, "testData"), location);
        else
            Assert.Equal("~/testData", location);
    }

    [Fact]
    public void Location_OfAFileDirectlyInHome_IsTheHomeFolder()
    {
        string location = DocumentStatusLine.Location(Path.Combine(Home, "big.json"));

        Assert.Equal(OperatingSystem.IsWindows() ? Home : "~", location);
    }

    [Fact]
    public void Location_DoesNotShortenAFolderThatOnlyStartsLikeHome()
    {
        // "/Users/marc-other" starts with "/Users/marc" but is not inside it.
        string sibling = Home + "-other";

        string location = DocumentStatusLine.Location(Path.Combine(sibling, "big.json"));

        Assert.Equal(sibling, location);
    }

    [Fact]
    public void Location_OfADocumentThatIsNotAFile_IsItsName()
    {
        // A paste or a download has a display name, not a path.
        Assert.Equal("Pasted data", DocumentStatusLine.Location("Pasted data"));
    }
}
