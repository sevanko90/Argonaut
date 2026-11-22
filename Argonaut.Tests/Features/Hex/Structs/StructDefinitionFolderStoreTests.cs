using Argonaut.Features.Hex.Structs;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class StructDefinitionFolderStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void ADefinition_IsSavedAsAFileInItsLanguage_AndReadBack()
    {
        var store = new StructDefinitionFolderStore(dir);

        var c = store.Save("Header", StructLanguage.C, "struct H { int a; };");
        var cs = store.Save("Header", StructLanguage.CSharp, "struct H { public int A; }");

        Assert.Equal(Path.Combine(dir, "Header.h"), c.FilePath);
        Assert.Equal(Path.Combine(dir, "Header.cs"), cs.FilePath);
        Assert.Equal([c, cs], store.Enumerate());
        Assert.Equal("struct H { int a; };", store.Load(c));
    }

    [Fact]
    public void SavingUnderTheSameName_Replaces()
    {
        var store = new StructDefinitionFolderStore(dir);
        store.Save("Pair", StructLanguage.C, "struct P { int a; };");
        var again = store.Save("Pair", StructLanguage.C, "struct P { int b; };");

        Assert.Single(store.Enumerate());
        Assert.Equal("struct P { int b; };", store.Load(again));
    }

    [Fact]
    public void AHeaderDroppedIntoTheFolder_IsListed_AndOtherFilesAreNot()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "packet.hpp"), "struct Packet { char c; };");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a definition");

        var entry = Assert.Single(new StructDefinitionFolderStore(dir).Enumerate());
        Assert.Equal(("packet", StructLanguage.C), (entry.Name, entry.Language));
    }

    [Fact]
    public void ANameWithCharactersNoFileSystemTakes_IsMadeSafe()
    {
        var saved = new StructDefinitionFolderStore(dir).Save(" a/b:c ", StructLanguage.C, "struct A { int a; };");
        Assert.Equal("a_b_c", saved.Name);
    }

    [Fact]
    public void AMissingFolder_ListsNothing_AndRevealCreatesIt()
    {
        string? shown = null;
        var store = new StructDefinitionFolderStore(dir, folder => shown = folder);
        Assert.Empty(store.Enumerate());

        store.RevealFolder();
        Assert.Equal(dir, shown);
        Assert.True(Directory.Exists(dir));
    }
}
