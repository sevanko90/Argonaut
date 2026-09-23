using System;
using System.IO;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Saving;

/// <summary>
/// An <see cref="IFileReplacer"/> that stages the new content in a hidden file beside the
/// destination - the one place guaranteed to be on its volume, which a rename needs to be atomic.
/// This class is the part that is the same everywhere: resolving the destination, sweeping
/// abandoned stages, checking for room, and the stage's lifetime. What differs by platform is four
/// questions, each answered by a subclass:
///
///   <see cref="FreeSpaceFor"/>       how much room the destination's volume has
///   <see cref="CarryOverPermissions"/> what the new file must inherit from the old one
///   <see cref="FlushToDisk"/>        what "on stable storage" takes
///   <see cref="SwapIn"/>             how the staged file replaces the destination
///
/// See <see cref="WindowsFileReplacer"/>, <see cref="UnixFileReplacer"/> and
/// <see cref="MacFileReplacer"/>; <see cref="ForCurrentPlatform"/> picks one. None of them works
/// under the Mac App Store sandbox, which forbids a file beside the original - docs/save-plan.md.
///
/// A symlink is followed: the file it points at is replaced and the link is left alone. Hard links
/// are broken by any rename-based save, and that is accepted.
/// </summary>
public abstract class SiblingFileReplacer : IFileReplacer
{
    /// <summary>Marks a staged file, so an abandoned one (a crash mid-save) can be recognised and
    /// swept by a later save to the same folder.</summary>
    internal const string StageMarker = ".argonaut-save-";

    /// <summary>
    /// How old an abandoned stage must be before a save sweeps it. A save in progress keeps
    /// writing to its stage, so its modification time stays fresh; an hour without a write is a
    /// stage nobody is coming back for, not another instance's save that is still running.
    /// </summary>
    internal static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    /// <summary>The replacer for the platform this process is running on. The only place that
    /// asks which platform that is.</summary>
    public static SiblingFileReplacer ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsFileReplacer();

        if (OperatingSystem.IsMacOS())
            return new MacFileReplacer();

        return new UnixFileReplacer();
    }

    /// <summary>Bytes free on the volume holding <paramref name="directory"/>. May throw; the
    /// space check is then skipped rather than failing the save.</summary>
    protected abstract long FreeSpaceFor(string directory);

    /// <summary>Gives the new file whatever the destination has that a rename would otherwise
    /// lose. Called only when the destination already exists.</summary>
    protected abstract void CarryOverPermissions(string destination, FileStream staged);

    /// <summary>Makes everything written to <paramref name="staged"/> durable. Runs on the
    /// background copy, before the stream is closed.</summary>
    protected virtual void FlushToDisk(FileStream staged) => staged.Flush(flushToDisk: true);

    /// <summary>Replaces <paramref name="destination"/> (which may not exist yet) with
    /// <paramref name="staged"/>, atomically. The staged file is sealed and closed by now.</summary>
    protected abstract void SwapIn(string staged, string destination);

    public StagedFile Stage(IByteOrigin destination, long contentLength)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);

        string target = ResolveTarget(destination.Path
            ?? throw new ArgumentException("A document with no path has nowhere to be replaced; save it as a file instead.", nameof(destination)));

        string directory = Path.GetDirectoryName(target)
            ?? throw new IOException($"\"{target}\" has no containing folder.");
        string name = Path.GetFileName(target);

        SweepAbandonedStages(directory, name);
        EnsureRoomFor(directory, contentLength);

        string staged = Path.Combine(directory, $".{name}{StageMarker}{Path.GetRandomFileName()}");
        var content = new FileStream(staged, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            // The writer hands over spans straight from the mapping in multi-megabyte runs, so a
            // FileStream buffer would only add a copy.
            BufferSize = 0,
            // Reserves the space up front where the OS can, so running out shows up now rather
            // than most of the way through a multi-GB copy.
            PreallocationSize = contentLength,
        });

        try
        {
            if (File.Exists(target))
                CarryOverPermissions(target, content);
        }
        catch
        {
            content.Dispose();
            TryDelete(staged);
            throw;
        }

        return new SiblingStagedFile(this, content, staged, target);
    }

    /// <summary>The file a save to <paramref name="path"/> should actually replace: the final
    /// target of a symlink, so the link survives as a link.</summary>
    private static string ResolveTarget(string path)
    {
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved)
            return resolved.FullName;

        return full;
    }

    /// <summary>
    /// Fails before anything is written when the volume cannot hold the new content alongside the
    /// old - a save needs both at once until the swap. Failing at 90% of a multi-GB copy wastes a
    /// minute and leaves a stage to clean up. Best effort: a volume that cannot report its free
    /// space is let through, and preallocation or the copy itself will say so instead.
    /// </summary>
    private void EnsureRoomFor(string directory, long contentLength)
    {
        long free;
        try
        {
            free = FreeSpaceFor(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (free < contentLength)
        {
            throw new IOException(
                $"Not enough free space to save: {FormatSize(contentLength)} needed, {FormatSize(free)} free on that drive.");
        }
    }

    /// <summary>
    /// Deletes stages a crashed save left beside <paramref name="name"/>. Only that file's, and
    /// only old ones - see <see cref="AbandonedAfter"/>. Failures are ignored: a stage that cannot
    /// be deleted is still open somewhere, which is its owner's business.
    /// </summary>
    private static void SweepAbandonedStages(string directory, string name)
    {
        string[] stages;
        try
        {
            stages = Directory.GetFiles(directory, $".{name}{StageMarker}*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - AbandonedAfter;
        foreach (string stage in stages)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(stage) < cutoff)
                    File.Delete(stage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0.0} KB",
    };

    private sealed class SiblingStagedFile : StagedFile
    {
        private readonly SiblingFileReplacer platform;
        private readonly FileStream content;
        private readonly string staged;
        private readonly string target;
        private bool isSealed;
        private bool committed;
        private bool kept;

        public SiblingStagedFile(SiblingFileReplacer platform, FileStream content, string staged, string target)
        {
            this.platform = platform;
            this.content = content;
            this.staged = staged;
            this.target = target;
        }

        public override Stream Content => this.content;

        public override string Location => this.staged;

        public override void Seal()
        {
            if (this.isSealed)
                return;

            this.platform.FlushToDisk(this.content);
            this.content.Dispose();
            this.isSealed = true;
        }

        public override void Commit()
        {
            if (this.committed)
                throw new InvalidOperationException("Already committed.");

            Seal();
            this.platform.SwapIn(this.staged, this.target);
            this.committed = true;
        }

        public override void KeepStagedContent() => this.kept = true;

        public override void Dispose()
        {
            this.content.Dispose();
            if (!this.committed && !this.kept)
                TryDelete(this.staged);
        }
    }
}
