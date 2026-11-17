using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;

namespace Argonaut.Tests.Features.Hex;

/// <summary>The hex toolbar names what the bytes are, from their signature and their source, and
/// hands the document's own origin to the shell's save and open actions.</summary>
public sealed class HexToolbarViewModelTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private static async Task<HexToolbarViewModel> ToolbarFor(IByteOrigin origin, IDocumentExportActions? actions = null)
    {
        var hex = new HexViewModel(actions);
        await hex.LoadAsync(origin);
        return Assert.IsType<HexToolbarViewModel>(hex.Toolbar);
    }

    [Fact]
    public async Task ASignedFormat_IsNamed()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        Assert.Equal("PNG image", (await ToolbarFor(origin)).Description);
    }

    [Fact]
    public async Task Text_HasNothingToName()
    {
        using var origin = new MemoryByteOrigin("""{"a":1}"""u8.ToArray(), "pasted");
        var toolbar = await ToolbarFor(origin);

        Assert.Null(toolbar.Description);
        Assert.False(toolbar.HasDescription);
    }

    [Fact]
    public void TheSourcesMediaType_FollowsTheFormat()
    {
        using var origin = new TypedOrigin(Png, "image/png");
        Assert.Equal("PNG image · image/png", new HexToolbarViewModel(origin, "PNG image", null).Description);
        Assert.Equal("image/png", new HexToolbarViewModel(origin, null, null).Description);
    }

    [Fact]
    public async Task WithoutActions_NoneAreOffered()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        var toolbar = await ToolbarFor(origin);

        Assert.False(toolbar.OffersActions);
        await toolbar.SaveCopyAsync();
    }

    [Fact]
    public async Task TheActions_AreHandedTheDocumentsOrigin()
    {
        var actions = new RecordingActions();
        using var origin = new MemoryByteOrigin(Png, "logo");
        var toolbar = await ToolbarFor(origin, actions);

        Assert.True(toolbar.OffersActions);
        await toolbar.SaveCopyAsync();
        await toolbar.OpenInDefaultAppAsync();

        Assert.Equal([("save", (IByteOrigin)origin), ("open", origin)], actions.Calls);
    }

    private sealed class RecordingActions : IDocumentExportActions
    {
        public List<(string, IByteOrigin)> Calls { get; } = [];

        public Task SaveCopyAsync(IByteOrigin origin)
        {
            Calls.Add(("save", origin));
            return Task.CompletedTask;
        }

        public Task OpenInDefaultAppAsync(IByteOrigin origin)
        {
            Calls.Add(("open", origin));
            return Task.CompletedTask;
        }
    }

    /// <summary>A pasted-bytes origin that says what it is, as a response does.</summary>
    private sealed class TypedOrigin(byte[] bytes, string mediaType) : IByteOrigin, IDisposable
    {
        private readonly MemoryByteOrigin inner = new(bytes, "typed");

        public string DisplayName => inner.DisplayName;

        public SourceLocator? Locator => null;

        public long AvailableLength => inner.AvailableLength;

        public string? MediaType => mediaType;

        public IByteSource Open() => inner.Open();

        public IByteSource OpenRange(long offset, long length) => inner.OpenRange(offset, length);

        public KeptIndexes KeptIndexes => inner.KeptIndexes;

        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task TheGroupingSelector_SetsTheDocumentsGrouping_AndRemembersIt()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        var settings = new HexViewSettings();
        var hex = new HexViewModel(null, settings);
        await hex.LoadAsync(origin);
        var toolbar = Assert.IsType<HexToolbarViewModel>(hex.Toolbar);
        var changed = new List<string?>();
        toolbar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(0, toolbar.GroupSizeIndex);
        Assert.Equal("1 byte", toolbar.GroupSizeText);

        toolbar.GroupSizeIndex = 2;

        Assert.Equal(4, hex.GroupSize);
        Assert.Equal(4, settings.GroupSize);
        Assert.Equal("4 bytes", toolbar.GroupSizeText);
        Assert.Contains(nameof(HexToolbarViewModel.GroupSizeText), changed);
    }

    [Fact]
    public void ARememberedGroupingThatIsNoSize_ReadsAsBytesOneByOne()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize("""{ "groupSize": 3 }""", HexViewSettings.JsonTypeInfo)!;
        Assert.Equal(1, settings.GroupSize);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize("{}", HexViewSettings.JsonTypeInfo)!.GroupSize);
        Assert.Equal(8, System.Text.Json.JsonSerializer.Deserialize("""{ "GroupSize": 8 }""", HexViewSettings.JsonTypeInfo)!.GroupSize);
    }

    [Fact]
    public async Task TheTextSelector_ReadsUtf8_AndRemembersIt()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        var settings = new HexViewSettings();
        var hex = new HexViewModel(null, settings);
        await hex.LoadAsync(origin);
        var toolbar = Assert.IsType<HexToolbarViewModel>(hex.Toolbar);

        Assert.Equal("ASCII", toolbar.TextEncodingText);
        toolbar.TextEncodingIndex = 1;

        Assert.True(hex.ReadsUtf8);
        Assert.True(settings.ReadsUtf8);
        Assert.Equal("UTF-8", toolbar.TextEncodingText);
    }
}
