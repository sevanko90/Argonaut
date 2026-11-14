using System;

namespace Argonaut.Engine.Settings;

/// <summary>
/// Seals a settings block that should not be readable from <c>settings.json</c> - saved sources,
/// whose URLs can carry API keys, and the schema bindings keyed by them. One implementation per
/// platform, chosen in the composition root, each keeping its key with the OS rather than the app
/// so the sealed blocks outlive a reinstall.
/// </summary>
public interface ISettingsSealer
{
    /// <summary>The sealed text for <paramref name="content"/>, or null when there is no key to
    /// seal with - the caller then leaves the block as it was rather than writing it unsealed.</summary>
    string? Seal(ReadOnlySpan<byte> content);

    /// <summary>What <paramref name="sealedText"/> holds, or null for anything this sealer did not
    /// seal with its current key - tampered, foreign, or sealed on another machine. Never throws:
    /// a block that will not open is an empty block.</summary>
    byte[]? Open(string sealedText);
}

/// <summary>Where <see cref="AesGcmSettingsSealer"/>'s key lives: the macOS Keychain, or a
/// user-only file on Linux.</summary>
public interface ISealingKeyStore
{
    /// <summary>The stored key, or null when there is none or it cannot be read.</summary>
    byte[]? LoadKey();

    /// <summary>Stores <paramref name="key"/>; false when it could not be kept.</summary>
    bool SaveKey(byte[] key);
}
