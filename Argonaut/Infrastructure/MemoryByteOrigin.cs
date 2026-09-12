using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// A document that arrived as bytes rather than as a file - a clipboard paste, or a small
/// download. <see cref="Path"/> is null, which is what the path-shaped features (recent files,
/// the schema sidecar, save, reveal in folder) key off to degrade rather than guess a path.
///
/// Every source it hands out is a window over the one array, so <see cref="OpenRange"/> costs
/// nothing: a sub-document inside a paste is a view, never a copy. Nothing holds an OS resource,
/// so <see cref="Dispose"/> only drops the reference - the array is collected once the origin and
/// every source over it are unreachable, which is why a source outliving its origin (a search
/// still scanning after the document closed) is safe here in a way a temp-file spill is not.
///
/// Above some size a paste or download should spill to a temp file and be served by
/// <see cref="FileByteOrigin"/> instead, so the tuned multi-GB mapped path stays the one in use
/// and the length stays OS-reported (see CLAUDE.md). This type is for the payloads small enough
/// that one managed array is the cheaper answer.
/// </summary>
public sealed class MemoryByteOrigin : IByteOrigin
{
    private readonly byte[] bytes;

    public MemoryByteOrigin(byte[] bytes, string displayName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        this.bytes = bytes;
        DisplayName = displayName;
    }

    public string DisplayName { get; }

    /// <summary>Always null: these bytes are not a file, and pretending otherwise is what makes
    /// save, reload and "open containing folder" act on something that isn't there.</summary>
    public string? Path => null;

    public long AvailableLength => this.bytes.Length;

    /// <summary>Always true - every byte is already present, so nothing is still arriving.</summary>
    public bool LengthSettled => true;

    public IByteSource Open() => new MemoryByteSource(this.bytes);

    public IByteSource OpenRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return new MemoryByteSource(this.bytes, checked((int)offset), checked((int)length));
    }

    public void Dispose()
    {
        // Nothing to release. The array is managed, and a source still reading it (a search that
        // outlived the document) keeps it alive by itself.
    }
}
