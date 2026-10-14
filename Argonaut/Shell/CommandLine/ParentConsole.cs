using System;
using System.Runtime.InteropServices;

namespace Argonaut.Shell.CommandLine;

/// <summary>
/// Where command-line output can go before any window exists. On macOS and Linux the process
/// inherits the streams of whatever launched it: a terminal's, or - from the Dock, Finder or a
/// desktop launcher - <c>/dev/null</c>, so output is only seen when one of stdout and stderr is
/// still a terminal (either is enough: <c>--help | less</c> leaves stderr on it). On Windows the app is a
/// <c>WinExe</c>, which gets no console of its own: output only reaches the terminal it was
/// started from by attaching to that terminal's console - and when there is none (a shortcut, an
/// "Open with"), nothing printed would ever be seen, so the caller shows it in a window instead.
/// </summary>
public static class ParentConsole
{
    private const int AttachParentProcess = -1;

    /// <summary>True when what is written to <see cref="Console"/> will be seen.</summary>
    public static bool TryAttach()
    {
        if (!OperatingSystem.IsWindows())
            return !Console.IsOutputRedirected || !Console.IsErrorRedirected;

        if (!AttachConsole(AttachParentProcess))
            return false;

        // The standard streams were bound to nothing at startup; rebind them to the console
        // just attached.
        Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        // The prompt was printed before this process wrote anything: start on a fresh line.
        Console.WriteLine();
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
