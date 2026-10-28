using System;

namespace Argonaut.Ui.Launching;

/// <summary>
/// Hands a folder or a web address to the operating system to open in another app - the file
/// manager, the browser, the mail client. Both are best-effort: a failure to open never reaches
/// the caller.
///
/// It is an interface because how the app may ask differs by platform: macOS goes through
/// <c>NSWorkspace</c>, which the Mac App Store sandbox allows, and elsewhere the request is
/// shell-executed. The shell's <c>SystemLaunchers.ForThisBuild</c> picks the implementation;
/// the views reach it through their view models, which is what keeps a link click testable.
/// </summary>
public interface ISystemLauncher
{
    /// <summary>Shows <paramref name="path"/> in the platform's file manager.</summary>
    void ShowFolder(string path);

    /// <summary>Opens <paramref name="address"/> (http, https or mailto) in the app the user
    /// has chosen for it.</summary>
    void OpenAddress(Uri address);
}
