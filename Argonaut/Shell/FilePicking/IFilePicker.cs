using System.Threading.Tasks;

namespace Argonaut.Shell.FilePicking;

/// <summary>Asks the user for a file to open. The shell goes through this rather than the
/// Avalonia storage provider so a platform that can show hidden files - where config files
/// live - can supply its own dialog.</summary>
public interface IFilePicker
{
    /// <param name="title">The dialog's title.</param>
    /// <param name="startFolder">The folder the dialog opens in, or null for the platform's default.</param>
    /// <returns>The chosen file's local path, or null if the user cancelled or the choice has no
    /// local path.</returns>
    Task<string?> PickFileAsync(string title, string? startFolder = null);
}
