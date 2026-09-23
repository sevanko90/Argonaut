using System;
using System.Threading;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// A document's bytes, held so that a save can let go of the mapping underneath them for a moment
/// and put an equivalent one back. Everything that reads the document - the row index, the piece
/// table, the row collection - holds this rather than the mapping, so one <see cref="Remap"/>
/// repoints all of them at once.
///
/// The reason is Windows: a file cannot be replaced while any mapping of it is open, so a save
/// has to <see cref="Unmap"/> before it swaps the new content in. If the swap then fails, the file
/// is untouched by contract, and the user's edits - which are logical offsets into these very
/// bytes - must survive over a freshly opened mapping of the same file rather than be thrown away.
///
/// Between <see cref="Unmap"/> and <see cref="Remap"/> every read throws
/// <see cref="ObjectDisposedException"/>, exactly as a released <see cref="MMapFile"/> does. The
/// save does both on the UI thread in one synchronous step, with no background reader running, so
/// nothing is meant to observe that window.
/// </summary>
public sealed class RemappableByteSource : IByteSource, IDisposable
{
    private IByteSource? mapped;

    /// <param name="mapped">The source to read through; owned from here on.</param>
    public RemappableByteSource(IByteSource mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        this.mapped = mapped;
    }

    /// <summary>False between <see cref="Unmap"/> and <see cref="Remap"/>, and after release.</summary>
    public bool IsMapped => this.mapped is not null;

    public long AvailableLength => Current.AvailableLength;

    public bool LengthSettled => Current.LengthSettled;

    public void WaitForLength(long atLeast, CancellationToken cancellationToken) => Current.WaitForLength(atLeast, cancellationToken);

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength) => Current.GetContiguousSpan(offset, maxLength);

    public int CopyTo(long offset, Span<byte> destination) => Current.CopyTo(offset, destination);

    /// <summary>Releases the source underneath. Every read fails until <see cref="Remap"/>.</summary>
    public void Unmap()
    {
        var released = this.mapped;
        this.mapped = null;
        released?.Release();
    }

    /// <summary>
    /// Reads through <paramref name="replacement"/> from now on, which must be the same bytes the
    /// source held before <see cref="Unmap"/>. Length is the check that can be made cheaply, and
    /// every offset anything holds into this source only means the same thing over bytes of the
    /// same length. Owned from here on; released here instead if it is refused.
    /// </summary>
    public void Remap(IByteSource replacement, long expectedLength)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (this.mapped is not null || replacement.AvailableLength != expectedLength)
        {
            bool alreadyMapped = this.mapped is not null;
            long found = replacement.AvailableLength;
            replacement.Release();
            throw new InvalidOperationException(alreadyMapped
                ? "Remap is only valid after Unmap."
                : $"The file is now {found:N0} bytes; it was {expectedLength:N0}. It changed on disk, so the edits no longer line up with it.");
        }

        this.mapped = replacement;
    }

    /// <summary>Releases the source underneath, if one is mapped. Idempotent.</summary>
    public void Dispose() => Unmap();

    private IByteSource Current => this.mapped ?? throw new ObjectDisposedException(nameof(RemappableByteSource));
}
