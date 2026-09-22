using System.IO;
using System.Runtime.Versioning;

namespace Argonaut.Infrastructure;

/// <summary>
/// Saving on Windows, portable and MSIX alike (MSIX runs full trust, so there is no store branch).
/// The swap is <c>File.Replace</c> - Win32 <c>ReplaceFile</c> - which keeps the destination's
/// attributes, ACLs and alternate data streams, so there is nothing to carry over by hand.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileReplacer : SiblingFileReplacer
{
    /// <summary><c>DriveInfo</c> on Windows wants a drive root, not a folder.</summary>
    protected override long FreeSpaceFor(string directory) =>
        new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;

    /// <summary>Nothing to do: <see cref="SwapIn"/>'s ReplaceFile keeps the destination's ACLs
    /// and attributes.</summary>
    protected override void CarryOverPermissions(string destination, FileStream staged)
    {
    }

    /// <summary>ReplaceFile needs a file to replace; a Save As to a new name is a plain move.</summary>
    protected override void SwapIn(string staged, string destination)
    {
        if (File.Exists(destination))
            File.Replace(staged, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(staged, destination);
    }
}
