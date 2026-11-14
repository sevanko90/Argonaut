using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Engine.Http;

/// <summary>
/// <see cref="IHttpFetcher"/> over one shared <see cref="HttpClient"/> - built once in the
/// composition root, as <c>HttpClient</c> is meant to be. Reads only the headers before checking
/// the status, then copies the body in chunks, so nothing about a response is held in memory.
/// </summary>
public sealed class HttpClientFetcher(HttpClient client) : IHttpFetcher
{
    private const int ChunkBytes = 81920;

    public async Task FetchAsync(HttpRequestSpec request, Stream destination, Action<long, long?> received, CancellationToken stopping)
    {
        using var message = new HttpRequestMessage(request.Method == HttpRequestMethod.Post ? HttpMethod.Post : HttpMethod.Get, request.Url);
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
                throw new HttpFetchException((int)response.StatusCode, response.ReasonPhrase ?? response.StatusCode.ToString());

            long? total = response.Content.Headers.ContentLength;
            try
            {
                await using var arriving = await response.Content.ReadAsStreamAsync(stopping);
                var buffer = new byte[ChunkBytes];
                long written = 0;
                int read;
                while ((read = await arriving.ReadAsync(buffer, stopping)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), stopping);
                    written += read;
                    received(written, total);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !stopping.IsCancellationRequested)
            {
                throw new HttpFetchException($"The response from {request.Url.Host} stopped arriving: {ex.Message}", ex);
            }
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
