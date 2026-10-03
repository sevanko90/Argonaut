using Argonaut.Shell;

namespace Argonaut.Tests.Shell;

public sealed class FavouriteFilesTests
{
    private static readonly string FirstPath = Path.GetFullPath(Path.Combine("cfg", "first.ini"));
    private static readonly string SecondPath = Path.GetFullPath(Path.Combine("cfg", "second.ini"));

    [Fact]
    public void Toggle_PinsInOrderAndNeverCaps()
    {
        var favourites = new FavouriteFiles();

        for (var i = 0; i < 12; i++)
            favourites.Toggle(Path.GetFullPath($"file{i}.json"));

        Assert.Equal(12, favourites.Paths.Count);
        Assert.Equal(Path.GetFullPath("file0.json"), favourites.Paths[0]);
        Assert.Equal(Path.GetFullPath("file11.json"), favourites.Paths[11]);
    }

    [Fact]
    public void Toggle_OfAPinnedFileUnpinsIt()
    {
        var favourites = new FavouriteFiles();
        favourites.Toggle(FirstPath);
        favourites.Toggle(SecondPath);

        favourites.Toggle(FirstPath);

        Assert.Equal([SecondPath], favourites.Paths);
        Assert.False(favourites.Contains(FirstPath));
    }

    [Fact]
    public void Paths_DropsBlanksAndDuplicatesFromAHandEditedFile()
    {
        var favourites = new FavouriteFiles { Paths = [FirstPath, "", "  ", FirstPath.ToUpperInvariant(), SecondPath] };

        Assert.Equal([FirstPath, SecondPath], favourites.Paths);
    }

    [Fact]
    public void Paths_AcceptsNull()
    {
        var favourites = new FavouriteFiles { Paths = null! };

        Assert.Empty(favourites.Paths);
    }
}
