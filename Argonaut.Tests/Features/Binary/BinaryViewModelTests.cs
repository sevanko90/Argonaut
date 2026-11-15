using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Features.Binary;

namespace Argonaut.Tests.Features.Binary;

/// <summary>A binary document described: its format from its signature, its size, and the two
/// actions handed to the shell with the document's own origin.</summary>
public sealed class BinaryViewModelTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [Fact]
    public async Task ASignedFormat_IsNamed_WithItsSize()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        using var document = new BinaryViewModel(null);

        await document.LoadAsync(origin);

        Assert.Equal("PNG image", document.Title);
        Assert.Equal("12 bytes", document.Detail);
        Assert.Same(origin, document.Origin);
        Assert.True(document.CanHandleFileType(FileTypeDetector.FileKind.Binary));
        Assert.Null(document.CreateSearchNavigator());
    }

    [Fact]
    public async Task UnknownBytes_AreBinaryData()
    {
        using var origin = new MemoryByteOrigin([1, 0, 2, 0], "blob");
        using var document = new BinaryViewModel(null);

        await document.LoadAsync(origin);

        Assert.Equal("Binary data", document.Title);
    }

    [Fact]
    public async Task WithoutActions_NoneAreOffered()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        using var document = new BinaryViewModel(null);
        await document.LoadAsync(origin);

        Assert.False(document.OffersActions);
        await document.SaveCopyAsync();
    }

    [Fact]
    public async Task TheActions_AreHandedTheDocumentsOrigin()
    {
        var actions = new RecordingActions();
        using var origin = new MemoryByteOrigin(Png, "logo");
        using var document = new BinaryViewModel(actions);
        await document.LoadAsync(origin);

        Assert.True(document.OffersActions);
        await document.SaveCopyAsync();
        await document.OpenInDefaultAppAsync();

        Assert.Equal([("save", origin), ("open", origin)], actions.Calls);
    }

    private sealed class RecordingActions : IBinaryDocumentActions
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
}
