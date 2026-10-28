#if !MAC_APP_STORE
using System;
using System.Diagnostics;
using Argonaut.Ui.Launching;

namespace Argonaut.Shell.Launching;

/// <summary>
/// Opens folders and addresses by shell-executing them, which every desktop platform maps to its
/// default handler. Windows and Linux use it, and macOS falls back to it only if the Objective-C
/// runtime cannot be reached. Not compiled into the Mac App Store build: the sandbox does not
/// allow it, and leaving it out means nothing there can reach it by mistake.
/// </summary>
public sealed class ProcessSystemLauncher : ISystemLauncher
{
    public void ShowFolder(string path) => Launch(path);

    public void OpenAddress(Uri address) => Launch(address.AbsoluteUri);

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Best-effort - a missing handler must never crash what asked.
        }
    }
}
#endif
