using System.Net;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Http;
using Argonaut.Engine.Progress;

namespace Argonaut.Tests.Engine.Http;

/// <summary>
/// Downloading a response into the response folder and opening it as an origin: progress as it
/// arrives, a stopped or failed download leaving nothing behind, and the origin reading what
/// arrived under the request's locator.
/// </summary>
public sealed class ResponseDownloaderTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static HttpRequestSpec Request(string url = "https://api.example.com/items")
    {
        HttpRequestSpec.TryCreate(HttpRequestMethod.Get, url, null, out var request, out _);
        return request!;
    }

    private string[] FilesLeft() => Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

    [Fact]
    public async Task ADownload_OpensAsAnOriginOverWhatArrived()
    {
        var fetcher = new FakeHttpFetcher(Encoding.UTF8.GetBytes("""{"items":[1,2,3]}"""), chunk: 4, announceLength: true);
        var downloader = new ResponseDownloader(fetcher, folder);

        using var origin = await downloader.DownloadAsync(Request(), progress: null, CancellationToken.None);

        Assert.Equal(17, origin.AvailableLength);
        var source = origin.Open();
        try
        {
            Assert.Equal("""{"items":[1,2,3]}""", Encoding.UTF8.GetString(source.RequireContiguous(0, 17)));
        }
        finally
        {
            source.Release();
        }
        var locator = Assert.IsType<HttpLocator>(origin.Locator);
        Assert.Equal(Request().Url, locator.Request.Url);
        Assert.Equal("items · api.example.com", origin.DisplayName);
    }

    [Fact]
    public async Task Progress_CountsBytes_AgainstTheLengthWhenTheServerGivesOne()
    {
        var withLength = new RecordingProgress();
        var withoutLength = new RecordingProgress();

        using (await new ResponseDownloader(new FakeHttpFetcher(new byte[10], chunk: 4, announceLength: true), folder)
            .DownloadAsync(Request(), withLength, CancellationToken.None)) { }
        using (await new ResponseDownloader(new FakeHttpFetcher(new byte[10], chunk: 4, announceLength: false), folder)
            .DownloadAsync(Request(), withoutLength, CancellationToken.None)) { }

        Assert.Equal((10L, (long?)10L), withLength.Reports[^1]);
        Assert.Contains(withLength.Reports, r => r.Current == 4 && r.Max == 10);
        Assert.Equal((10L, (long?)null), withoutLength.Reports[^1]);
    }

    [Fact]
    public async Task AStoppedDownload_LeavesNothingBehind()
    {
        using var stopping = new CancellationTokenSource();
        var fetcher = new FakeHttpFetcher(new byte[100], chunk: 10, announceLength: true) { AfterChunk = received => { if (received >= 30) stopping.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ResponseDownloader(fetcher, folder).DownloadAsync(Request(), null, stopping.Token));

        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task AFailedRequest_SaysWhy_AndLeavesNothingBehind()
    {
        var fetcher = new FakeHttpFetcher([], chunk: 1, announceLength: false) { Status = HttpStatusCode.NotFound };

        var failure = await Assert.ThrowsAsync<HttpFetchException>(
            () => new ResponseDownloader(fetcher, folder).DownloadAsync(Request(), null, CancellationToken.None));

        Assert.Contains("404", failure.Message);
        Assert.Contains("Not Found", failure.Message);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task DisposingTheOrigin_DeletesItsFile()
    {
        var origin = await new ResponseDownloader(new FakeHttpFetcher(new byte[5], 5, true), folder).DownloadAsync(Request(), null, CancellationToken.None);
        Assert.Single(FilesLeft());

        origin.Dispose();

        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task TheResponseFile_IsReadableByTheUserOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var origin = await new ResponseDownloader(new FakeHttpFetcher(new byte[5], 5, true), folder).DownloadAsync(Request(), null, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Assert.Single(FilesLeft())));
    }

    private sealed class RecordingProgress : IProgressReporter
    {
        public List<(long Current, long? Max)> Reports { get; } = [];

        public void Report(string message, long? current = null, long? max = null)
        {
            if (current is { } value)
                Reports.Add((value, max));
        }
    }
}

/// <summary>A server in a test: answers with <c>body</c> in chunks, optionally saying its length up
/// front, with a status of the test's choosing.</summary>
internal sealed class FakeHttpFetcher(byte[] body, int chunk, bool announceLength) : IHttpFetcher
{
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

    public Action<long>? AfterChunk { get; init; }

    public List<HttpRequestSpec> Sent { get; } = [];

    public async Task FetchAsync(HttpRequestSpec request, Stream destination, Action<long, long?> received, CancellationToken stopping)
    {
        Sent.Add(request);
        if ((int)Status is < 200 or > 299)
            throw new HttpFetchException((int)Status, Status == HttpStatusCode.NotFound ? "Not Found" : Status.ToString());

        long total = 0;
        for (int at = 0; at < body.Length; at += chunk)
        {
            stopping.ThrowIfCancellationRequested();
            int length = Math.Min(chunk, body.Length - at);
            await destination.WriteAsync(body.AsMemory(at, length), stopping);
            total += length;
            received(total, announceLength ? body.Length : null);
            AfterChunk?.Invoke(total);
        }

        stopping.ThrowIfCancellationRequested();
    }
}
