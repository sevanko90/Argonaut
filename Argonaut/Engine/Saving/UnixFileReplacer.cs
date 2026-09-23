using System.IO;
using System.Runtime.Versioning;

namespace Argonaut.Engine.Saving;

/// <summary>
/// Saving on Linux, and the base of <see cref="MacFileReplacer"/>. The swap is <c>rename(2)</c>,
/// which installs a new inode, so the destination's permission bits are copied onto the stage
/// first. Ownership, ACLs and extended attributes are not carried across.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class UnixFileReplacer : SiblingFileReplacer
{
    /// <summary><c>DriveInfo</c> on Unix takes any path and reports the volume holding it, which
    /// is the right one when the folder is a mount point.</summary>
    protected override long FreeSpaceFor(string directory) => new DriveInfo(directory).AvailableFreeSpace;

    /// <summary>Otherwise the new file's mode would be whatever the umask gave the stage.</summary>
    protected override void CarryOverPermissions(string destination, FileStream staged) =>
        File.SetUnixFileMode(staged.SafeFileHandle, File.GetUnixFileMode(destination));

    /// <summary><c>File.Move</c> with overwrite is <c>rename(2)</c> on Unix: atomic on one volume.</summary>
    protected override void SwapIn(string staged, string destination) =>
        File.Move(staged, destination, overwrite: true);
}
