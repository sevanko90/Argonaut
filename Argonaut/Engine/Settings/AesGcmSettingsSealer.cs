using System;
using System.Security.Cryptography;

namespace Argonaut.Engine.Settings;

/// <summary>
/// <see cref="ISettingsSealer"/> with AES-256-GCM under a key from an <see cref="ISealingKeyStore"/>,
/// created on the first seal. The sealed text is <see cref="Prefix"/> then base64 of nonce, tag and
/// ciphertext. GCM's tag is what makes a tampered or foreign blob open as nothing.
///
/// A key that could not be stored is never used: content sealed under it could not be opened on
/// the next run, which would lose it silently - so sealing reports there is no key instead.
/// </summary>
public sealed class AesGcmSettingsSealer(ISealingKeyStore keys) : ISettingsSealer
{
    public const string Prefix = "aesgcm1:";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly object gate = new();
    private byte[]? key;

    public string? Seal(ReadOnlySpan<byte> content)
    {
        if (KeyFor(create: true) is not { } current)
            return null;

        var sealedBytes = new byte[NonceBytes + TagBytes + content.Length];
        var nonce = sealedBytes.AsSpan(0, NonceBytes);
        var tag = sealedBytes.AsSpan(NonceBytes, TagBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(current, TagBytes);
        aes.Encrypt(nonce, content, sealedBytes.AsSpan(NonceBytes + TagBytes), tag);

        return Prefix + Convert.ToBase64String(sealedBytes);
    }

    public byte[]? Open(string sealedText)
    {
        if (sealedText is null || !sealedText.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        byte[] sealedBytes;
        try
        {
            sealedBytes = Convert.FromBase64String(sealedText[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (sealedBytes.Length < NonceBytes + TagBytes || KeyFor(create: false) is not { } current)
            return null;

        var content = new byte[sealedBytes.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(current, TagBytes);
            aes.Decrypt(
                sealedBytes.AsSpan(0, NonceBytes),
                sealedBytes.AsSpan(NonceBytes + TagBytes),
                sealedBytes.AsSpan(NonceBytes, TagBytes),
                content);
            return content;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The key, loaded once; with <paramref name="create"/>, made and stored when there is
    /// none. Opening never creates one - there is nothing a new key could open.</summary>
    private byte[]? KeyFor(bool create)
    {
        lock (gate)
        {
            if (key is not null)
                return key;

            if (keys.LoadKey() is { Length: KeyBytes } loaded)
                return key = loaded;

            if (!create)
                return null;

            byte[] made = RandomNumberGenerator.GetBytes(KeyBytes);
            return keys.SaveKey(made) ? key = made : null;
        }
    }
}
