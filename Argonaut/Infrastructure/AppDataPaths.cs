using System;
using System.IO;

namespace Argonaut.Infrastructure;

internal static class AppDataPaths
{
    public static string GetSettingsFilePath(string fileName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argonaut", fileName);
}
