using System;

namespace Argonaut.Engine.Http;

/// <summary>The methods the request pane offers.</summary>
public enum HttpRequestMethod
{
    Get,
    Post,
}

/// <summary>
/// One request ready to send: the method, an absolute http or https URL, and - for POST only -
/// the body. The pane edits a draft and asks for one of these when Send is pressed; anything that
/// cannot be sent comes back as a sentence saying why, for the pane to show.
/// </summary>
public sealed record HttpRequestSpec
{
    private HttpRequestSpec(HttpRequestMethod method, Uri url, byte[]? body)
    {
        Method = method;
        Url = url;
        Body = body;
    }

    public HttpRequestMethod Method { get; }

    public Uri Url { get; }

    /// <summary>The bytes sent as the body; always null for GET.</summary>
    public byte[]? Body { get; }

    /// <param name="body">Ignored for GET, which sends none.</param>
    public static bool TryCreate(HttpRequestMethod method, string urlText, byte[]? body, out HttpRequestSpec? request, out string? problem)
    {
        request = null;
        string text = urlText?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            problem = "Enter a URL to send a request to.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            problem = "Start the URL with http:// or https://.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
        {
            problem = $"\"{text}\" is not a valid URL.";
            return false;
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            problem = "Start the URL with http:// or https://.";
            return false;
        }

        problem = null;
        request = new HttpRequestSpec(method, url, method == HttpRequestMethod.Post ? body ?? [] : null);
        return true;
    }
}
