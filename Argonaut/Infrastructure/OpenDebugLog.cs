using System;
using System.IO;

namespace Argonaut.Infrastructure;

// Temporary diagnostics for tracking down why "Open With" file activation doesn't load
// the file on macOS. Remove once that flow is confirmed working end-to-end.
internal static class OpenDebugLog
{
    private static readonly string LogPath = AppDataPaths.GetSettingsFilePath("open-debug.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }
}
