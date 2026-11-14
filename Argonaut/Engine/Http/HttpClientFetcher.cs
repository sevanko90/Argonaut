using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Engine.Http;

/// <summary>
/// <see cref="IHttpFetcher"/> over one shared <see cref="HttpClient"/> - built once in the
/// composition root, as <c>HttpClient</c> is meant to be. Reads only the headers before checking
/// the status, then copies the body in chunks, so nothing about a response is held in memory.
///
/// Compression is handled here rather than by the client's handler: every request accepts gzip,
/// deflate and Brotli, and a response that uses one (many APIs always do) is decompressed as it
/// streams to the file. Progress counts the bytes as they come over the wire, since that is what
/// <c>Content-Length</c> measures.
/// </summary>
public sealed class HttpClientFetcher(HttpClient client) : IHttpFetcher
{
    private const int ChunkBytes = 81920;

    public async Task FetchAsync(HttpRequestSpec request, Stream destination, Action<long, long?> received, CancellationToken stopping)
    {
        using var message = new HttpRequestMessage(request.Method == HttpRequestMethod.Post ? HttpMethod.Post : HttpMethod.Get, request.Url);
        foreach (string encoding in AcceptedEncodings)
            message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(LooksLikeJson(body) ? "application/json" : "text/plain");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stopping);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpFetchException($"Couldn't reach {request.Url.Host}: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpFetchException((int)response.StatusCode, response.ReasonPhrase ?? response.StatusCode.ToString(),
                    await ReadServerMessageAsync(response, stopping));
            }

            long? total = response.Content.Headers.ContentLength;
            try
            {
                await using var arriving = await response.Content.ReadAsStreamAsync(stopping);
                var counted = new CountingReadStream(arriving);
                await using var decoded = Decode(counted, response.Content.Headers.ContentEncoding);
                var buffer = new byte[ChunkBytes];
                int read;
                while ((read = await decoded.ReadAsync(buffer, stopping)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), stopping);
                    received(counted.BytesRead, total);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException && !stopping.IsCancellationRequested)
            {
                throw new HttpFetchException($"The response from {request.Url.Host} stopped arriving: {ex.Message}", ex);
            }
            catch (NotSupportedException ex)
            {
                throw new HttpFetchException($"The response from {request.Url.Host} is compressed in a way this app cannot read ({ex.Message}).", ex);
            }
        }
    }

    private static readonly string[] AcceptedEncodings = ["gzip", "deflate", "br"];

    /// <summary>The start of a failure's body as text - up to
    /// <see cref="HttpFetchException.MaxServerMessageBytes"/>, decompressed - or null when it cannot
    /// be read. Best effort: the failure is the status, and this only explains it.</summary>
    private static async Task<byte[]?> ReadServerMessageAsync(HttpResponseMessage response, CancellationToken stopping)
    {
        try
        {
            await using var arriving = await response.Content.ReadAsStreamAsync(stopping);
            await using var decoded = Decode(arriving, response.Content.Headers.ContentEncoding);
            var buffer = new byte[HttpFetchException.MaxServerMessageBytes];
            int filled = 0, read;
            while (filled < buffer.Length && (read = await decoded.ReadAsync(buffer.AsMemory(filled), stopping)) > 0)
                filled += read;

            return buffer[..filled];
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="body"/> with each of the response's content codings undone, last applied
    /// first. <c>identity</c> is none; anything not understood is refused rather than written out
    /// as bytes the views would show as noise.
    /// </summary>
    private static Stream Decode(Stream body, ICollection<string> encodings)
    {
        foreach (string encoding in encodings.Reverse())
        {
            body = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(body, CompressionMode.Decompress),
                "deflate" => new ZLibStream(body, CompressionMode.Decompress),
                "br" => new BrotliStream(body, CompressionMode.Decompress),
                "identity" or "" => body,
                var other => throw new NotSupportedException(other),
            };
        }

        return body;
    }

    /// <summary>A read-only pass-through counting what is read from the wire, beneath any
    /// decompression.</summary>
    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Counted(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Counted(int read)
        {
            BytesRead += read;
            return read;
        }
    }

    /// <summary>What a POST body is sent as: JSON when it starts like JSON, text otherwise. The
    /// request manager's headers will let the user say; until then this guesses the common case.</summary>
    private static bool LooksLikeJson(byte[] body)
    {
        foreach (byte b in body)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b is (byte)'{' or (byte)'[';
        }

        return false;
    }
}
