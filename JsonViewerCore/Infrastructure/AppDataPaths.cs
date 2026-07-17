using System;
using System.IO;

namespace JsonViewerCore.Infrastructure;

internal static class AppDataPaths
{
    public static string GetSettingsFilePath(string fileName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BigJsonViewer", fileName);
}
