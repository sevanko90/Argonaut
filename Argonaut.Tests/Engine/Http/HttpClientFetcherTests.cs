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

    [Theory]
    [InlineData("text/csv; charset=utf-8", "text/csv")]
    [InlineData("image/png", "image/png")]
    [InlineData(null, null)]
    public async Task TheResponsesMediaType_IsReturned_WithoutItsParameters(string? header, string? expected)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "a,b") { ContentType = header };

        string? mediaType = await new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Get), new MemoryStream(), (_, _) => { }, CancellationToken.None);

        Assert.Equal(expected, mediaType);
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
    public async Task AFailureStatus_KeepsWhatTheServerSaid_Decompressed()
    {
        byte[] said = Compress(Encoding.UTF8.GetBytes("""{"error":"invalid key"}"""), "gzip");
        var handler = new StubHandler(HttpStatusCode.Forbidden, said, "gzip");

        var failure = await Assert.ThrowsAsync<HttpFetchException>(() =>
            new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Get), new MemoryStream(), (_, _) => { }, CancellationToken.None));

        Assert.Equal(403, failure.StatusCode);
        Assert.Equal("""{"error":"invalid key"}""", failure.ServerMessage);
    }

    [Fact]
    public async Task AFailureStatusWithALongBody_KeepsOnlyItsStart()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, new string('x', 200_000));

        var failure = await Assert.ThrowsAsync<HttpFetchException>(() =>
            new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Get), new MemoryStream(), (_, _) => { }, CancellationToken.None));

        Assert.Equal(HttpFetchException.MaxServerMessageBytes, failure.ServerMessage!.Length);
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

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task ACompressedResponse_IsWrittenDecompressed_WithProgressInCompressedBytes(string encoding)
    {
        string json = string.Concat(Enumerable.Repeat("""{"productType":["0"],"current_SALES_gte":1000}""", 50));
        byte[] compressed = Compress(Encoding.UTF8.GetBytes(json), encoding);
        var handler = new StubHandler(HttpStatusCode.OK, compressed, encoding);
        using var destination = new MemoryStream();
        var seen = new List<(long Received, long? Total)>();

        await new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Get), destination, (r, t) => seen.Add((r, t)), CancellationToken.None);

        Assert.Equal(json, Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal((compressed.LongLength, (long?)compressed.LongLength), seen[^1]);
    }

    [Fact]
    public async Task EveryRequest_SaysWhichCompressionsItAccepts()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        await new HttpClientFetcher(new HttpClient(handler)).FetchAsync(Request(HttpRequestMethod.Get), new MemoryStream(), (_, _) => { }, CancellationToken.None);

        Assert.Equal(["gzip", "deflate", "br"], handler.Sent!.Headers.AcceptEncoding.Select(e => e.Value));
    }

    private static byte[] Compress(byte[] plain, string encoding)
    {
        using var packed = new MemoryStream();
        using (Stream compressor = encoding switch
        {
            "gzip" => new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true),
            "deflate" => new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true),
            _ => new System.IO.Compression.BrotliStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true),
        })
        {
            compressor.Write(plain);
        }

        return packed.ToArray();
    }

    private sealed class StubHandler(HttpStatusCode status, byte[] body, string? contentEncoding = null) : HttpMessageHandler
    {
        public StubHandler(HttpStatusCode status, string body)
            : this(status, Encoding.UTF8.GetBytes(body))
        {
        }

        public HttpRequestMessage? Sent { get; private set; }

        public string? SentBody { get; private set; }

        public string? SentContentType { get; private set; }

        /// <summary>The response's <c>Content-Type</c> header as sent, or null for none.</summary>
        public string? ContentType { get; init; } = "application/json";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = request;
            if (request.Content is { } content)
            {
                SentBody = await content.ReadAsStringAsync(cancellationToken);
                SentContentType = content.Headers.ContentType?.MediaType;
            }

            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentLength = body.Length;
            response.Content.Headers.ContentType = ContentType is null ? null : MediaTypeHeaderValue.Parse(ContentType);
            if (contentEncoding is not null)
                response.Content.Headers.ContentEncoding.Add(contentEncoding);
            return response;
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }
}
