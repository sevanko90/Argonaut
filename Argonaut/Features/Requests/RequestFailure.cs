using Argonaut.Engine.Http;

namespace Argonaut.Features.Requests;

/// <summary>
/// What the response area shows instead of a response when a send fails: the status as a title
/// ("403 Forbidden"), a sentence on what it usually means, and what the server said - an API's
/// error body is usually the real explanation. Shown in place of the last response, so a failure
/// can never look like the same answer coming back.
/// </summary>
public sealed record RequestFailure(string Title, string Explanation, string? ServerMessage)
{
    public static RequestFailure From(HttpFetchException failure)
    {
        if (failure.StatusCode is not { } status)
            return new RequestFailure("No response", failure.Message, null);

        return new RequestFailure($"{status} {failure.Reason}", Explain(status), failure.ServerMessage);
    }

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
