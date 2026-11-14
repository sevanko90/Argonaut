using System.Threading.Tasks;
using Argonaut.Engine.Http;

namespace Argonaut.Features.Requests;

/// <summary>
/// What sends the request pane's request: the shell, which downloads the response under a
/// progress entry and opens it as the document below the pane. Lives here, with the pane that
/// consumes it, so the feature never reaches into the shell.
/// </summary>
public interface IRequestSender
{
    /// <summary>Sends <paramref name="request"/> and opens what comes back; completes when the
    /// response has opened, failed, or been stopped. Never throws - a failure is reported to the
    /// user by the sender.</summary>
    Task SendAsync(HttpRequestSpec request);
}
