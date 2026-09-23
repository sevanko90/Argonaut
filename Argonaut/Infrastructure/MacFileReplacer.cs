using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Argonaut.Infrastructure;

/// <summary>
/// Saving on macOS outside the App Store sandbox: <see cref="UnixFileReplacer"/>, plus the one
/// thing macOS needs for the content to be durable. Finder tags and other extended attributes are
/// still lost to the rename; the <c>NSFileManager</c> replacer that would keep them is queued in
/// docs/save-plan.md.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacFileReplacer : UnixFileReplacer
{
    /// <summary><c>fcntl</c> command asking macOS to flush the drive's own write cache, which
    /// plain <c>fsync</c> does not - without it, a power cut after the rename can leave the
    /// renamed file with content that never reached the disk.</summary>
    private const int FullFsync = 51;

    /// <summary>The ordinary fsync, then <c>F_FULLFSYNC</c>. Best effort: a file system that does
    /// not support it (a network share) has already had the fsync.</summary>
    protected override void FlushToDisk(FileStream staged)
    {
        base.FlushToDisk(staged);
        _ = fcntl((int)staged.SafeFileHandle.DangerousGetHandle(), FullFsync, 0);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
