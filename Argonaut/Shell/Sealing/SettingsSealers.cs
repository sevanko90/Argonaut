using System;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sealing;

/// <summary>Chooses this platform's <see cref="ISettingsSealer"/> - see <c>SystemLaunchers</c> for
/// the same shape.</summary>
public static class SettingsSealers
{
    /// <param name="keyFilePath">Where Linux keeps its key file - the config folder.</param>
    public static ISettingsSealer ForThisPlatform(string keyFilePath)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSettingsSealer();

        if (OperatingSystem.IsMacOS())
            return new AesGcmSettingsSealer(new KeychainSealingKeyStore(KeychainSealingKeyStore.DefaultService, KeychainSealingKeyStore.DefaultAccount));

        return new AesGcmSettingsSealer(new FileSealingKeyStore(keyFilePath));
    }
}
