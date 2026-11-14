using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sealing;

/// <summary>
/// <see cref="ISettingsSealer"/> on Windows through DPAPI, current-user scope: Windows derives the
/// key from the user's own credentials, so there is no key for the app to keep, and anything
/// sealed opens for that user on that machine after a reinstall.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSettingsSealer : ISettingsSealer
{
    public const string Prefix = "dpapi1:";

    // Ties a blob to this purpose, so another app's DPAPI data does not open here as settings.
    private static readonly byte[] Purpose = "Argonaut.Settings"u8.ToArray();

    public string? Seal(ReadOnlySpan<byte> content)
    {
        try
        {
            return Prefix + Convert.ToBase64String(ProtectedData.Protect(content.ToArray(), Purpose, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public byte[]? Open(string sealedText)
    {
        if (sealedText is null || !sealedText.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(sealedText[Prefix.Length..]), Purpose, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
