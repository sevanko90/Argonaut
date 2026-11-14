using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Engine.Http;

/// <summary>
/// Sends a request and streams its response body into a stream it is given - the one place the
/// app touches the network, injected so tests stand in a server. Never buffers the body: a
/// response is written as it arrives, however large.
/// </summary>
public interface IHttpFetcher
{
    /// <param name="received">Told the bytes written so far, and the total when the server said
    /// it (<c>Content-Length</c>), after each write. Called on the fetching thread.</param>
    /// <exception cref="HttpFetchException">The server answered with anything but 2xx, or could not
    /// be reached - nothing has been written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="stopping"/> fired.</exception>
    Task FetchAsync(HttpRequestSpec request, Stream destination, Action<long, long?> received, CancellationToken stopping);
}

/// <summary>Why a request produced no response worth showing, as a sentence for the user.</summary>
public sealed class HttpFetchException : Exception
{
    /// <summary>How much of a failure's body is kept: enough for any error message an API
    /// writes, never a whole document someone served with an error status.</summary>
    public const int MaxServerMessageBytes = 64 * 1024;

    /// <summary>The server answered, with a failure status - and, usually, a body saying why.</summary>
    public HttpFetchException(int statusCode, string reason, byte[]? serverMessage = null)
        : base($"The server answered {statusCode} {reason}.")
    {
        StatusCode = statusCode;
        Reason = reason;
        ServerMessageBytes = serverMessage is { Length: > 0 } ? serverMessage : null;
    }

    /// <summary>The server could not be reached, or the answer could not be read.</summary>
    public HttpFetchException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>The failure status, or null when no answer came.</summary>
    public int? StatusCode { get; }

    /// <summary>The status's reason phrase ("Forbidden"), or null when no answer came.</summary>
    public string? Reason { get; }

    /// <summary>The start of the failure's body, decompressed - an API's own explanation, often
    /// JSON - or null when it sent none.</summary>
    public byte[]? ServerMessageBytes { get; }

    /// <summary><see cref="ServerMessageBytes"/> as text.</summary>
    public string? ServerMessage => ServerMessageBytes is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
}
