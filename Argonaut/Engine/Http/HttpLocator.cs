using System;
using System.Security.Cryptography;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Http;

/// <summary>
/// A request as the place a document comes from: what a response's origin reports, what a saved
/// source keeps, and what reopening sends again.
///
/// Identity is the method and the URL as the server reads it - scheme and host case-folded, the
/// fragment dropped (it never leaves the client) - and for a POST its body, by hash, so two POSTs
/// to one URL with different bodies are two places.
/// </summary>
public sealed record HttpLocator(HttpRequestSpec Request) : SourceLocator
{
    /// <summary>"items · api.example.com" - the last path segment and the host, or the host alone.</summary>
    public override string DefaultName
    {
        get
        {
            string segment = Request.Url.Segments.Length > 0 ? Request.Url.Segments[^1].Trim('/') : string.Empty;
            segment = Uri.UnescapeDataString(segment);
            return segment.Length == 0 ? Request.Url.Host : $"{segment} · {Request.Url.Host}";
        }
    }

    public override string DisplayText => $"{MethodName} {Request.Url.OriginalString.Trim()}";

    public override string Identity
    {
        get
        {
            string url = Request.Url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            string identity = $"http:{MethodName} {url}";
            return Request.Body is { } body ? identity + " body:" + Convert.ToHexString(SHA256.HashData(body)) : identity;
        }
    }

    private string MethodName => Request.Method.ToString().ToUpperInvariant();
}
