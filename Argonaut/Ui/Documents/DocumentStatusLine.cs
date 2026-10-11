using System;
using System.IO;

namespace Argonaut.Ui.Documents;

/// <summary>
/// The status bar's line for a document: where it is, then what the view found
/// (<c>~/testData · 25.1 MB</c>). The file's name is the title bar's, so it is not repeated here;
/// the full path is the status bar's tooltip.
/// </summary>
public static class DocumentStatusLine
{
    private const string Separator = " · ";

    /// <summary><paramref name="detail"/> after the folder holding <paramref name="filePath"/>.</summary>
    public static string Compose(string filePath, string detail) => Location(filePath) + Separator + detail;

    /// <summary>
    /// The folder holding <paramref name="filePath"/>, with the home folder written <c>~</c> outside
    /// Windows, where that is the convention. A document that is not a file (a paste, a download)
    /// has a display name instead of a path, and that name is its location.
    /// </summary>
    public static string Location(string filePath)
    {
        if (!Path.IsPathRooted(filePath) || Path.GetDirectoryName(filePath) is not { Length: > 0 } folder)
            return filePath;

        if (OperatingSystem.IsWindows())
            return folder;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0)
            return folder;

        if (folder == home)
            return "~";

        return folder.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + folder[home.Length..]
            : folder;
    }
}
