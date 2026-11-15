using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>A response origin deletes its file when closed - unless the response was kept.</summary>
public sealed class HttpResponseOriginTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"response-{Guid.NewGuid():N}.response");

    public void Dispose() => File.Delete(path);

    private static HttpLocator Locator()
    {
        HttpRequestSpec.TryCreate(HttpRequestMethod.Get, "https://x.test/a", null, out var request, out _);
        return new HttpLocator(request!);
    }

    [Fact]
    public void AKeptResponse_OutlivesItsOrigin()
    {
        File.WriteAllText(path, "{}");
        var origin = new HttpResponseOrigin(Locator(), path, mediaType: null);

        origin.MarkKept();
        origin.Dispose();

        Assert.True(File.Exists(path));
        Assert.Equal(Path.GetFileName(path), origin.ResponseFileName);
    }

    [Fact]
    public void AKeptFileReopened_IsNotDeletedByItsOrigin()
    {
        File.WriteAllText(path, "{}");

        new HttpResponseOrigin(Locator(), path, mediaType: null, kept: true).Dispose();

        Assert.True(File.Exists(path));
    }
}
