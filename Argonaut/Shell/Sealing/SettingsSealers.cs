using System;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sealing;

/// <summary>Chooses this platform's <see cref="ISettingsSealer"/> - see <c>SystemLaunchers</c> for
/// the same shape. Windows seals with DPAPI; macOS and Linux keep an AES-GCM key in a user-only
/// file. macOS does not use the Keychain: the data-protection Keychain needs a restricted
/// entitlement and a provisioning profile, and vpk signs every binary in the bundle with the
/// app's entitlements - its updater included, which macOS then kills at launch for want of a
/// profile. The login Keychain needs neither, but prompts whenever the app's signature changes.
/// Sealing keeps secrets out of a shared or backed-up settings file, not away from processes
/// running as the same user, so a file only the user can read is enough.</summary>
public static class SettingsSealers
{
    /// <param name="keyFilePath">Where the key file lives - the config folder.</param>
    public static ISettingsSealer ForThisPlatform(string keyFilePath) =>
        OperatingSystem.IsWindows()
            ? new DpapiSettingsSealer()
            : new AesGcmSettingsSealer(new FileSealingKeyStore(keyFilePath));
}
