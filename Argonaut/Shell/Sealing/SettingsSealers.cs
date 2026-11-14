using System;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sealing;

/// <summary>Chooses this platform's <see cref="ISettingsSealer"/> - see <c>SystemLaunchers</c> for
/// the same shape. A Debug build on macOS keeps its key in the file the Linux build uses: a dev
/// build is ad-hoc signed with a new signature every build, and a Keychain item's access list is
/// tied to the signature of the app that made it, so every rebuild would raise the Keychain prompt
/// - and an item a dev build made raises it for the released app too.</summary>
public static class SettingsSealers
{
    /// <param name="keyFilePath">Where Linux, and a Debug build on macOS, keeps its key file - the
    /// config folder.</param>
    public static ISettingsSealer ForThisPlatform(string keyFilePath)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSettingsSealer();

#if !DEBUG
        if (OperatingSystem.IsMacOS())
            return new AesGcmSettingsSealer(new KeychainSealingKeyStore(KeychainSealingKeyStore.DefaultService, KeychainSealingKeyStore.DefaultAccount));
#endif

        return new AesGcmSettingsSealer(new FileSealingKeyStore(keyFilePath));
    }
}
