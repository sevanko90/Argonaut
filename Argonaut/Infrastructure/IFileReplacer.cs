using System;
using System.IO;

namespace Argonaut.Infrastructure;

/// <summary>
/// Stages new content for a file and swaps it in atomically, or not at all. The seam every write
/// to a user's file goes through, because the only portable part of a save is the copy: where the
/// staged content may live, how the swap is made atomic, and what metadata survives it all differ
/// by platform, and the Mac App Store sandbox forbids the obvious version outright. The writer
/// never touches the file system itself; it writes to <see cref="StagedFile.Content"/>.
///
/// See docs/save-plan.md for what every implementation must get right.
/// </summary>
public interface IFileReplacer
{
    /// <summary>
    /// Creates somewhere to write <paramref name="contentLength"/> bytes of new content for
    /// <paramref name="destination"/>, on the same volume and where this process may write. The
    /// destination need not exist yet (Save As). Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> - with a message fit to show - when that is not
    /// possible, including when the volume has no room for it; nothing has been written then.
    /// </summary>
    /// <param name="destination">Where the content is going. Must have a <see cref="IByteOrigin.Path"/>.</param>
    StagedFile Stage(IByteOrigin destination, long contentLength);
}

/// <summary>
/// New content on its way to a destination. Written through <see cref="Content"/>, made durable
/// by <see cref="Seal"/>, swapped in by <see cref="Commit"/>. Disposing a stage that was not
/// committed deletes it, unless <see cref="KeepStagedContent"/> said not to.
///
/// <see cref="Seal"/> is separate from <see cref="Commit"/> so the slow half - flushing gigabytes
/// to stable storage - can run on the background with the writer, leaving the commit itself a
/// rename, which is what the caller has to do with the document's mapping released.
/// </summary>
public abstract class StagedFile : IDisposable
{
    /// <summary>Where the writer writes. Sequential; the caller does not buffer.</summary>
    public abstract Stream Content { get; }

    /// <summary>Where the staged content is on disk, for telling the user where to find it if a
    /// commit fails in a way that leaves it the only copy.</summary>
    public abstract string Location { get; }

    /// <summary>Flushes the content to stable storage and closes it for writing. Idempotent.
    /// Safe to call off the UI thread.</summary>
    public abstract void Seal();

    /// <summary>
    /// Seals if not already sealed, then swaps the staged content in for the destination. Either
    /// the destination is the new content afterwards, or it is untouched and this throws.
    /// </summary>
    public abstract void Commit();

    /// <summary>Stops <see cref="Dispose"/> deleting the staged content - for when a failed commit
    /// has left it the only copy of what the user was saving.</summary>
    public abstract void KeepStagedContent();

    /// <summary>Closes the content and, unless committed or kept, deletes it.</summary>
    public abstract void Dispose();
}
