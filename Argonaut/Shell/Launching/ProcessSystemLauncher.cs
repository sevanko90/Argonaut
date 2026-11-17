#if !MAC_APP_STORE
using System;
using System.Diagnostics;
using System.IO;
using Argonaut.Ui.Launching;

namespace Argonaut.Shell.Launching;

/// <summary>
/// Opens folders, files and addresses by shell-executing them, which every desktop platform maps
/// to its default handler, and reveals a file with Explorer's <c>/select</c> (or <c>open -R</c> on
/// macOS). Windows and Linux use it, and macOS falls back to it only if the Objective-C
/// runtime cannot be reached. Not compiled into the Mac App Store build: the sandbox does not
/// allow it, and leaving it out means nothing there can reach it by mistake.
/// </summary>
public sealed class ProcessSystemLauncher : ISystemLauncher
{
    public void ShowFolder(string path) => Launch(path);

    public void OpenFile(string path) => Launch(path);

    public void RevealFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Arguments, not ArgumentList: Explorer has its own command-line parser, and the
                // quoting ArgumentList applies to a path with a space wraps the whole
                // "/select,..." switch, which Explorer does not recognise - it then opens its
                // default folder (Documents) instead. GetFullPath turns '/' into '\', which
                // /select also needs.
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = ExplorerSelectArguments(Path.GetFullPath(path)) })?.Dispose();
            }
            catch
            {
                // Best-effort, as Launch is.
            }

            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } })?.Dispose();
            }
            catch
            {
                // Best-effort, as Launch is.
            }

            return;
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Launch(folder);
    }

    /// <summary>Explorer's switch to open a file's folder with it selected: the path quoted on its
    /// own after the comma. A Windows path cannot contain a quote, so it needs no escaping.</summary>
    internal static string ExplorerSelectArguments(string path) => "/select,\"" + path + "\"";

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
