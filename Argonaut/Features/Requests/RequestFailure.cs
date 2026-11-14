using System;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Http;
using Argonaut.Ui.Documents;

namespace Argonaut.Features.Requests;

/// <summary>
/// What the response area shows instead of a response when a send fails: the status as a title
/// ("403 Forbidden"), a sentence on what it usually means, and what the server said - an API's
/// error body is usually the real explanation - shown through the app's own views where the shell
/// can host them (<see cref="Message"/>), as plain text otherwise. Shown in place of the last
/// response, so a failure can never look like the same answer coming back. Owns the hosted view.
/// </summary>
public sealed class RequestFailure : IDisposable
{
    private RequestFailure(string title, string explanation, string? serverMessage, ServerMessageViewModel? message)
    {
        Title = title;
        Explanation = explanation;
        ServerMessage = serverMessage;
        Message = message;
    }

    public string Title { get; }

    public string Explanation { get; }

    /// <summary>What the server said, as text; null when it said nothing.</summary>
    public string? ServerMessage { get; }

    /// <summary>What the server said in a hosted view (JSON/XML tree, or raw), or null when it
    /// said nothing or nothing can host it.</summary>
    public ServerMessageViewModel? Message { get; }

    /// <summary>Whether the server's words are shown as plain text, for want of a hosted view.</summary>
    public bool ShowsPlainMessage => Message is null && ServerMessage is not null;

    /// <param name="hostedLoader">Loads a hosted view of a kind over an origin; null shows the
    /// server's words as plain text.</param>
    public static RequestFailure From(HttpFetchException failure,
        Func<FileTypeDetector.FileKind, IByteOrigin, Task<IDocumentViewModel>>? hostedLoader = null)
    {
        if (failure.StatusCode is not { } status)
            return new RequestFailure("No response", failure.Message, null, null);

        var message = failure.ServerMessageBytes is { } bytes && hostedLoader is not null
            ? new ServerMessageViewModel(bytes, hostedLoader)
            : null;
        return new RequestFailure($"{status} {failure.Reason}", Explain(status), failure.ServerMessage, message);
    }

    public void Dispose() => Message?.Dispose();

    private static string Explain(int status) => status switch
    {
        400 => "The server says the request is malformed - check the arguments and the body.",
        401 => "The server wants credentials this request doesn't send.",
        403 => "The server refused the request - a key or credentials may be missing, wrong or without access.",
        404 => "There is nothing at this URL.",
        405 => "This URL doesn't accept this method - try the other one.",
        408 or 504 => "The request took too long to answer - try again.",
        413 => "The body is larger than the server accepts.",
        415 => "The server doesn't accept the body in this format.",
        429 => "Too many requests - wait a while before sending again.",
        >= 500 => "The server failed while answering - this is on its side; try again later.",
        _ => "The server didn't answer the request successfully.",
    };
}
