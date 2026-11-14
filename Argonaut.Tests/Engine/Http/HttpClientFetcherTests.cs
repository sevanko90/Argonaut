using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>The real fetcher over <see cref="HttpClient"/>, against a handler standing in for the
/// network: what it sends, how it streams the body back, and how it reports a failure.</summary>
public sealed class HttpClientFetcherTests
{
    private static HttpRequestSpec Request(HttpRequestMethod method, string? body = null)
    {
        HttpRequestSpec.TryCreate(method, "https://api.example.com/items?page=2", body is null ? null : Encoding.UTF8.GetBytes(body), out var request, out _);
        return request!;
    }

    [Fact]
    public async Task AGet_IsSentAsAGet_AndItsBodyStreamsToTheDestination()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "hello world");
        var fetcher = new HttpClientFetcher(new HttpClient(handler));
        using var destination = new MemoryStream();
        var seen = new List<(long, long?)>();

        await fetcher.FetchAsync(Request(HttpRequestMethod.Get), destination, (r, t) => seen.Add((r, t)), CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Sent!.Method);
        Assert.Equal("https://api.example.com/items?page=2", handler.Sent.RequestUri!.AbsoluteUri);
        Assert.Equal("hello world", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal((11L, (long?)11L), seen[^1]);
    }

    [Fact]
    public async Task APost_SendsItsBody_AsJsonWhenItLooksLikeJson()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        await new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Post, "  {\"q\":1}"), new MemoryStream(), (_, _) => { }, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Sent!.Method);
        Assert.Equal("  {\"q\":1}", handler.SentBody);
        Assert.Equal("application/json", handler.SentContentType);
    }

    [Fact]
    public async Task APostBodyThatIsNotJson_IsSentAsText()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        await new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Post, "a=1&b=2"), new MemoryStream(), (_, _) => { }, CancellationToken.None);

        Assert.Equal("text/plain", handler.SentContentType);
    }

    [Fact]
    public async Task AFailureStatus_ThrowsWithTheStatusLine_AndWritesNothing()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "denied");
        using var destination = new MemoryStream();

        var failure = await Assert.ThrowsAsync<HttpFetchException>(() =>
            new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Get), destination, (_, _) => { }, CancellationToken.None));

        Assert.Equal(401, failure.StatusCode);
        Assert.Contains("401 Unauthorized", failure.Message);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ANetworkFailure_ThrowsWithWhatWentWrong()
    {
        var fetcher = new HttpClientFetcher(new HttpClient(new FailingHandler()));

        var failure = await Assert.ThrowsAsync<HttpFetchException>(() =>
            fetcher.FetchAsync(Request(HttpRequestMethod.Get), new MemoryStream(), (_, _) => { }, CancellationToken.None));

        Assert.Null(failure.StatusCode);
        Assert.Contains("no route", failure.Message);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Sent { get; private set; }

        public string? SentBody { get; private set; }

        public string? SentContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = request;
            if (request.Content is { } content)
            {
                SentBody = await content.ReadAsStringAsync(cancellationToken);
                SentContentType = content.Headers.ContentType?.MediaType;
            }

            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(body);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }
}
