using System;
using System.Text;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// An action on a string that is wholly a web or mail address: "Open example.com" or
/// "Email someone@example.com", shown on the hovered or selected row, and the value itself made a
/// link for Cmd/Ctrl+click.
///
/// Only <c>http</c>, <c>https</c> and <c>mailto</c>: the file is untrusted, and any other scheme
/// would hand its value to whatever the system has registered for it. The action names the host
/// as the URI parser reads it, so <c>https://good.com@evil.com</c> says evil.com, and in its ASCII
/// form, so a look-alike letter from another script shows as the punycode it is.
/// </summary>
public sealed class UrlHintProvider : IValueHintProvider
{
    public bool IsActive => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || !HasSafeScheme(rawValue))
            return false;

        // Whole-value addresses only, written plainly: whitespace means text around it, and an
        // escape would need decoding before it could be checked.
        foreach (byte b in rawValue)
        {
            if (b <= (byte)' ' || b is (byte)'\\' or (byte)'"' or (byte)'<' or (byte)'>' or 0x7F)
                return false;
        }

        candidate = new ValueHintCandidate(ValueHintKind.Url, 0, 0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset)
    {
        if (!Uri.TryCreate(Encoding.UTF8.GetString(rawValue), UriKind.Absolute, out var address) || address.IdnHost.Length == 0)
            return null;

        var link = new OpenUrlLink(address);
        return address.Scheme == Uri.UriSchemeMailto
            ? new ValueHint($"Email {address.UserInfo}@{address.IdnHost}", TreeRunIcon.Mail, link, Style: TreeRunStyle.Action, ValueLink: link)
            : new ValueHint($"Open {address.IdnHost}", TreeRunIcon.OpenLink, link, Style: TreeRunStyle.Action, ValueLink: link);
    }

    /// <summary>Whether an address may be handed to the system: an <c>http</c>, <c>https</c> or
    /// <c>mailto</c> URI. Checked again where it is opened.</summary>
    public static bool IsSafe(Uri address)
        => address.IsAbsoluteUri && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeMailto);

    private static bool HasSafeScheme(ReadOnlySpan<byte> text)
        => StartsWithIgnoringCase(text, "https://"u8) || StartsWithIgnoringCase(text, "http://"u8) || StartsWithIgnoringCase(text, "mailto:"u8);

    private static bool StartsWithIgnoringCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> prefix)
    {
        if (text.Length <= prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if ((text[i] | 0x20) != prefix[i] && text[i] != prefix[i])
                return false;
        }

        return true;
    }

    /// <summary>An address does not depend on any setting, so its hints never go stale.</summary>
    public event EventHandler? HintsChanged
    {
        add { }
        remove { }
    }
}
