using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Features.Requests;
using Argonaut.Ui.Documents;

namespace Argonaut.Tests.Features.Requests;

/// <summary>
/// What a failed request's server said, shown through the app's own views: JSON and XML in their
/// tree (with Raw beside it), anything else - HTML included - as raw text.
/// </summary>
public sealed class ServerMessageViewModelTests
{
    private readonly List<(FileTypeDetector.FileKind Kind, string Text)> loads = [];

    private Task<IDocumentViewModel> Load(FileTypeDetector.FileKind kind, IByteOrigin origin)
    {
        var source = origin.Open();
        try
        {
            loads.Add((kind, Encoding.UTF8.GetString(source.RequireContiguous(0, (int)origin.AvailableLength))));
        }
        finally
        {
            source.Release();
        }

        return Task.FromResult<IDocumentViewModel>(new StubDocument(origin));
    }

    private ServerMessageViewModel Message(string text) => new(Encoding.UTF8.GetBytes(text), Load);

    [Theory]
    [InlineData("""{"error":"invalid key","code":403}""", FileTypeDetector.FileKind.Json)]
    [InlineData("""<?xml version="1.0"?><error><message>no</message></error>""", FileTypeDetector.FileKind.Xml)]
    public async Task StructuredMessages_OpenInTheirOwnView_WithRawOffered(string text, FileTypeDetector.FileKind kind)
    {
        using var message = Message(text);

        await message.ShowAsync();

        Assert.Equal(kind, message.DetectedKind);
        Assert.True(message.OffersViews);
        Assert.Equal((kind, text), Assert.Single(loads));
        Assert.NotNull(message.Shown.Document);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body><h1>403 Forbidden</h1></body></html>")]
    [InlineData("Forbidden: your key has expired")]
    public async Task EverythingElse_IsRawOnly(string text)
    {
        using var message = Message(text);

        await message.ShowAsync();

        Assert.Null(message.DetectedKind);
        Assert.False(message.OffersViews);
        Assert.Equal(FileTypeDetector.FileKind.Unidentified, Assert.Single(loads).Kind);
    }

    [Fact]
    public async Task SwitchingToRaw_AndBack_LoadsEachView()
    {
        using var message = Message("""{"error":"no"}""");
        await message.ShowAsync();

        message.ShowsRaw = true;
        await message.Shown.Loading;
        message.ShowsRaw = false;
        await message.Shown.Loading;

        Assert.Equal([FileTypeDetector.FileKind.Json, FileTypeDetector.FileKind.Unidentified, FileTypeDetector.FileKind.Json], loads.Select(l => l.Kind));
    }

    private sealed class StubDocument(IByteOrigin origin) : IDocumentViewModel
    {
        public IByteOrigin? Origin => origin;
        public string FilePath => origin.LocationText();
        public string StatusDetail => string.Empty;
        public object? Toolbar => null;
        public Argonaut.Ui.Find.ISearchNavigator? CreateSearchNavigator() => null;
        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;
        public Argonaut.Engine.Indexing.IndexFailure? IndexFailure => null;
        public Task IndexingTask => Task.CompletedTask;
        public bool Disposed { get; private set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Dispose() => Disposed = true;
    }
}
