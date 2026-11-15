using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Settings;
using Argonaut.Shell.Sources;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Shell.Sources;

/// <summary>
/// The saved sources: one list of recent and pinned entries, newest first, capped on the unpinned
/// ones only, and kept sealed - nothing about a source is readable from the settings file.
/// </summary>
public sealed class SavedSourceStoreTests
{
    private readonly MemorySealingKeyStore keys = new();

    private SavedSourceStore Store(SavedSourcesBlock? block = null)
        => new(block ?? new SavedSourcesBlock(), new AesGcmSettingsSealer(keys));

    private static FileLocator File(string name) => new(Path.Combine(Path.GetTempPath(), name));

    private static string[] Names(IEnumerable<SavedSource> entries) => entries.Select(e => e.Name).ToArray();

    [Fact]
    public void OpeningSources_ListsThemNewestFirst_NamedForTheirLocator()
    {
        var store = Store();

        store.Touch(File("a.json"));
        store.Touch(File("b.json"));

        Assert.Equal(["b.json", "a.json"], Names(store.Recent));
        Assert.Empty(store.Pinned);
    }

    [Fact]
    public void OpeningASourceAgain_MovesItToTheFront_WithoutADuplicate()
    {
        var store = Store();
        store.Touch(File("a.json"));
        store.Touch(File("b.json"));

        store.Touch(File("a.json"));

        Assert.Equal(["a.json", "b.json"], Names(store.Recent));
    }

    [Fact]
    public void TheCap_DropsTheOldestUnpinned_AndNeverAPin()
    {
        var store = Store();
        store.Touch(File("pinned.json"));
        store.TogglePin(store.Recent[0].Id);

        for (int i = 0; i < SavedSourceStore.MaxRecent + 2; i++)
            store.Touch(File($"r{i}.json"));

        Assert.Equal(SavedSourceStore.MaxRecent, store.Recent.Count);
        Assert.Equal("r6.json", store.Recent[0].Name);
        Assert.Equal(["pinned.json"], Names(store.Pinned));
    }

    [Fact]
    public void Pinning_MovesAnEntryBetweenTheLists_AndPinsKeepTheirOrder()
    {
        var store = Store();
        store.Touch(File("a.json"));
        store.Touch(File("b.json"));
        string a = store.Recent.Single(e => e.Name == "a.json").Id;
        string b = store.Recent.Single(e => e.Name == "b.json").Id;

        store.TogglePin(a);
        store.TogglePin(b);
        store.Touch(File("a.json"));

        Assert.Equal(["a.json", "b.json"], Names(store.Pinned));
        Assert.Empty(store.Recent);

        store.TogglePin(a);

        Assert.Equal(["b.json"], Names(store.Pinned));
        Assert.Equal(["a.json"], Names(store.Recent));
    }

    [Fact]
    public void Renaming_KeepsTheName_AcrossReopening()
    {
        var store = Store();
        store.Touch(File("a.json"));
        string id = store.Recent[0].Id;

        store.Rename(id, "  Orders feed  ");
        store.Touch(File("a.json"));

        Assert.Equal("Orders feed", store.Recent[0].Name);
    }

    [Fact]
    public void ABlankName_FallsBackToTheLocatorsName()
    {
        var store = Store();
        store.Touch(File("a.json"));
        string id = store.Recent[0].Id;

        store.Rename(id, "   ");

        Assert.Equal("a.json", store.Recent[0].Name);
    }

    [Fact]
    public void RemoveAndClear_TakeOutEntries_ClearLeavingPins()
    {
        var store = Store();
        store.Touch(File("a.json"));
        store.Touch(File("b.json"));
        store.Touch(File("c.json"));
        store.TogglePin(store.Recent.Single(e => e.Name == "c.json").Id);

        store.Remove(store.Recent.Single(e => e.Name == "a.json").Id);
        Assert.Equal(["b.json"], Names(store.Recent));

        store.ClearRecent();
        Assert.Empty(store.Recent);
        Assert.Equal(["c.json"], Names(store.Pinned));
    }

    [Fact]
    public void Entries_RoundTripThroughTheBlock_AndAreNotReadableInIt()
    {
        var block = new SavedSourcesBlock();
        var store = Store(block);
        store.Touch(File("secret-name.json"));
        store.TogglePin(store.Recent[0].Id);
        store.Touch(File("other.json"));

        string json = System.Text.Json.JsonSerializer.Serialize(block, SavedSourcesBlock.JsonTypeInfo);
        Assert.DoesNotContain("secret-name", json);
        Assert.DoesNotContain("other", json);

        var reread = System.Text.Json.JsonSerializer.Deserialize(json, SavedSourcesBlock.JsonTypeInfo)!;
        var reopened = Store(reread);

        Assert.Equal(["secret-name.json"], Names(reopened.Pinned));
        Assert.Equal(["other.json"], Names(reopened.Recent));
        Assert.True(File("other.json").IsSamePlace(reopened.Recent[0].Locator));
    }

    [Fact]
    public void ABlockThatWillNotOpen_StartsEmpty()
    {
        var block = new SavedSourcesBlock();
        Store(block).Touch(File("a.json"));

        var elsewhere = new SavedSourceStore(block, new AesGcmSettingsSealer(new MemorySealingKeyStore()));

        Assert.Empty(elsewhere.Recent);
    }

    [Fact]
    public void WithoutASealer_EntriesLiveInMemoryOnly()
    {
        var block = new SavedSourcesBlock();
        var store = new SavedSourceStore(block, sealer: null);

        store.Touch(File("a.json"));

        Assert.Equal(["a.json"], Names(store.Recent));
        Assert.Null(block.Sealed);
    }

    [Fact]
    public void AnEntryOfAKindThisBuildDoesNotKnow_IsKeptButNotListed()
    {
        var block = new SavedSourcesBlock();
        var sealer = new AesGcmSettingsSealer(keys);
        var list = new SavedSourceList { Entries = [new SavedSource { Id = "x", Name = "future", Kind = "ftp", Location = "ftp://h/x" }] };
        block.Sealed = sealer.Seal(SavedSourceList.Serialize(list));

        var store = new SavedSourceStore(block, sealer);
        store.Touch(File("a.json"));

        Assert.Equal(["a.json"], Names(store.Recent));
        var kept = SavedSourceList.Deserialize(sealer.Open(block.Sealed!)!);
        Assert.Contains(kept!.Entries, e => e.Kind == "ftp");
    }

    [Fact]
    public void ABookmark_IsKept_AndRefreshedByTheNextTouch()
    {
        var block = new SavedSourcesBlock();
        var store = Store(block);
        string path = Path.Combine(Path.GetTempPath(), "a.json");
        store.Touch(new FileLocator(path, bookmark: [1, 2, 3]));
        store.Touch(new FileLocator(path, bookmark: [4, 5]));

        var reopened = Store(block);

        Assert.Equal([4, 5], ((FileLocator)reopened.Recent[0].Locator!).Bookmark);
    }

    [Fact]
    public void Relocating_MovesAnEntryToItsNewPlace_KeepingItsNameAndPin()
    {
        var store = Store();
        store.Touch(File("old.json"));
        string id = store.Recent[0].Id;
        store.Rename(id, "Orders");
        store.TogglePin(id);

        store.Relocate(id, File("new.json"));

        var entry = Assert.Single(store.Pinned);
        Assert.Equal("Orders", entry.Name);
        Assert.True(File("new.json").IsSamePlace(entry.Locator));
    }

    [Fact]
    public void ARequest_IsSavedWithItsMethodAndBody_AndComesBackTheSame()
    {
        var block = new SavedSourcesBlock();
        Argonaut.Engine.Http.HttpRequestSpec.TryCreate(Argonaut.Engine.Http.HttpRequestMethod.Post, "https://api.example.com/search?x=1",
            System.Text.Encoding.UTF8.GetBytes("{\"q\":\"secret\"}"), out var request, out _);
        Store(block).Touch(new Argonaut.Engine.Http.HttpLocator(request!));

        string json = System.Text.Json.JsonSerializer.Serialize(block, SavedSourcesBlock.JsonTypeInfo);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("example", json);

        var entry = Assert.Single(Store(block).Recent);
        var reopened = Assert.IsType<Argonaut.Engine.Http.HttpLocator>(entry.Locator);
        Assert.Equal(Argonaut.Engine.Http.HttpRequestMethod.Post, reopened.Request.Method);
        Assert.Equal("{\"q\":\"secret\"}", System.Text.Encoding.UTF8.GetString(reopened.Request.Body!));
        Assert.Equal(SavedSource.HttpKind, entry.Kind);
    }

    private static Argonaut.Engine.Http.HttpLocator Request(string url)
    {
        Argonaut.Engine.Http.HttpRequestSpec.TryCreate(Argonaut.Engine.Http.HttpRequestMethod.Get, url, null, out var request, out _);
        return new Argonaut.Engine.Http.HttpLocator(request!);
    }

    [Fact]
    public void AKeptResponse_IsRecorded_AndReplacingItHandsBackTheOldFile()
    {
        var block = new SavedSourcesBlock();
        var store = Store(block);
        string id = store.Touch(Request("https://x.test/a")).Id;
        var at = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(store.SetResponse(id, "first.response", 10, at));
        Assert.Equal("first.response", store.SetResponse(id, "second.response", 20, at.AddMinutes(1)));

        var reopened = Store(block);
        var kept = Assert.Single(reopened.KeptResponses);
        Assert.Equal(("second.response", 20L, at.AddMinutes(1)), (kept.FileName, kept.Bytes, kept.FetchedAt));
        Assert.Equal("second.response", reopened.Find(id)!.ResponseFile);
    }

    [Fact]
    public void AKeptResponsesMediaType_IsRecorded_AndGoesWithTheResponse()
    {
        var block = new SavedSourcesBlock();
        var store = Store(block);
        string id = store.Touch(Request("https://x.test/a")).Id;

        store.SetResponse(id, "a.response", 3, DateTimeOffset.UnixEpoch, "image/png");
        Assert.Equal("image/png", Store(block).Find(id)!.ResponseMediaType);

        store.DropResponse(id);
        Assert.Null(Store(block).Find(id)!.ResponseMediaType);
    }

    [Fact]
    public void RemovingEntries_HandsBackTheirResponseFiles()
    {
        var store = Store();
        string a = store.Touch(Request("https://x.test/a")).Id;
        string b = store.Touch(Request("https://x.test/b")).Id;
        string pinned = store.Touch(Request("https://x.test/p")).Id;
        store.TogglePin(pinned);
        var at = DateTimeOffset.UnixEpoch;
        store.SetResponse(a, "a.response", 1, at);
        store.SetResponse(b, "b.response", 1, at);
        store.SetResponse(pinned, "p.response", 1, at);

        Assert.Equal(["a.response"], store.Remove(a));
        Assert.Equal(["b.response"], store.ClearRecent());
        Assert.Equal("p.response", store.DropResponse(pinned));
        Assert.Empty(store.KeptResponses);
    }

    [Fact]
    public void KeptResponses_SayWhichArePinned()
    {
        var store = Store();
        string a = store.Touch(Request("https://x.test/a")).Id;
        store.TogglePin(a);
        store.SetResponse(a, "a.response", 5, DateTimeOffset.UnixEpoch);

        Assert.True(Assert.Single(store.KeptResponses).Pinned);
    }
}
