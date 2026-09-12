using System;
using System.IO;

namespace Argonaut.Infrastructure;

/// <summary>
/// A document that is a file on disk - the origin every load took implicitly before origins
/// existed. Materialisation is a no-op (the bytes are already there), so every member is cheap
/// and the length is settled from birth.
///
/// It holds no handle of its own: each <see cref="Open"/>/<see cref="OpenRange"/> is an
/// independent <see cref="MMapFile"/> that its caller releases, and <see cref="Dispose"/> has
/// nothing to do. That is deliberate rather than lazy - it is what keeps a running search's chunk
/// mappings alive after the document that started the search has been closed, and what lets a
/// view swap map the file again while the outgoing view still holds its own mapping.
/// </summary>
public sealed class FileByteOrigin : IByteOrigin
{
    private readonly string path;

    public FileByteOrigin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = System.IO.Path.GetFullPath(path);
        DisplayName = System.IO.Path.GetFileName(this.path);
    }

    public string DisplayName { get; }

    public string? Path => this.path;

    /// <summary>
    /// The file's length, read from the file system on every access rather than cached: a file
    /// can be replaced or appended to while open, and this is the value every reader's bounds
    /// come from, so it must never be a stale snapshot or a mapping's rounded-up capacity (see
    /// CLAUDE.md).
    /// </summary>
    public long AvailableLength => new FileInfo(this.path).Length;

    /// <summary>Always true - every byte is already present, so nothing is still arriving.</summary>
    public bool LengthSettled => true;

    public IByteSource Open() => new MMapFile(this.path);

    public IByteSource OpenRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return new MMapFile(this.path, offset, length);
    }

    public void Dispose()
    {
        // Nothing to release: the file is the user's, and every source handed out is owned and
        // released by whoever asked for it.
    }
}
