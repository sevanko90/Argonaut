using System;
using Argonaut.Ui.Launching;

namespace Argonaut.Shell.Launching;

/// <summary>Chooses the <see cref="ISystemLauncher"/> this build may use.</summary>
public static class SystemLaunchers
{
    /// <summary>macOS goes through <c>NSWorkspace</c>; elsewhere the launcher shells out. The Mac
    /// App Store build (built with <c>-p:MacAppStore=true</c>, which defines
    /// <c>MAC_APP_STORE</c>) has no shell-out to fall back to: it is compiled out.</summary>
    public static ISystemLauncher ForThisBuild()
    {
#if MAC_APP_STORE
        return OperatingSystem.IsMacOS()
            ? new MacSystemLauncher(fallback: null)
            : throw new PlatformNotSupportedException("The Mac App Store build runs on macOS only.");
#else
        var shellOut = new ProcessSystemLauncher();
        return OperatingSystem.IsMacOS() ? new MacSystemLauncher(shellOut) : shellOut;
#endif
    }
}
