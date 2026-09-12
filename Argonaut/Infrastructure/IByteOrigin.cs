using System;
using System.Threading;

namespace Argonaut.Infrastructure;

/// <summary>
/// Where a document's bytes came from, and the only thing that can hand out an
/// <see cref="IByteSource"/> over them. The pair exists because the two have genuinely different
/// lifetimes, and collapsing them re-creates an ownership problem:
///
///   <b>An origin lives as long as the open input</b> - across view swaps included, which is what
///   stops switching from JSON to Raw re-downloading a URL or re-materialising a paste. It is
///   owned by the shell, one per input (two when diffing), and owns the materialised backing: the
///   file on disk, a temp-file spill, a pinned array.
///
///   <b>A source lives as long as one session</b>, which releases it. That contract is unchanged
///   and is what keeps teardown safe (see <see cref="IndexedSourceSession{TIndex}"/>): a session
///   joins its scans before releasing what it owns. Handing sessions a shared source instead
///   would turn that into a reference count, and getting a reference count wrong here is a native
///   use-after-free rather than an exception.
///
/// So a caller that needs bytes for a while asks for its own source and releases it; nobody
/// releases a source handed to them. Search is the call site that proves the split is necessary
/// rather than tidy: it scans on a background thread that is cancelled and forgotten, never
/// joined, so it must own every chunk it reads - which means it needs a factory, not a source.
/// </summary>
public interface IByteOrigin : IDisposable
{
    /// <summary>
    /// What the toolbar, status line and window title call this document. Always present, even
    /// when <see cref="Path"/> is not - a file's name, or something like "Pasted text".
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The file on disk these bytes are, or null when there is none - a paste, or a download
    /// served from memory. This is the one thing the path-shaped features consult (recent files,
    /// the <c>&lt;file&gt;.schema.json</c> sidecar, the remembered schema binding, save, reveal
    /// in folder), and the toolbar drives their enabled state off it once rather than each site
    /// null-checking.
    /// </summary>
    string? Path { get; }

    /// <summary>
    /// How much has arrived, whether that is final, and how to wait for more. Growth lives here
    /// rather than on the source because it is a property of the *arrival* of the bytes: a
    /// mapping is a fixed snapshot of a byte range and can never grow, so a whole-document source
    /// over a still-downloading origin forwards these, and a sub-range source is always settled
    /// (see <see cref="OpenRange"/>).
    /// </summary>
    long AvailableLength { get; }

    /// <inheritdoc cref="IByteSource.LengthSettled"/>
    bool LengthSettled => true;

    /// <inheritdoc cref="IByteSource.WaitForLength"/>
    void WaitForLength(long atLeast, CancellationToken cancellationToken) { }

    /// <summary>
    /// A source over the whole document, which the caller owns and must
    /// <see cref="ByteSourceReading.Release"/>. Reflects this origin's growth: over an origin
    /// still receiving data the returned source reports the same
    /// <see cref="AvailableLength"/>/<see cref="LengthSettled"/> as it does.
    ///
    /// Cheap and synchronous - materialisation happened when the origin was created, not here -
    /// so opening a second source for a view swap costs a mapping, not a download.
    /// </summary>
    IByteSource Open();
    /// <summary>
    /// A source over just [<paramref name="offset"/>, <paramref name="offset"/> +
    /// <paramref name="length"/>), zero-based, independent of any other source over this origin -
    /// one NDJSON line as a document in its own right, an array's byte range as a table, one
    /// search chunk. The caller owns it and must <see cref="ByteSourceReading.Release"/> it.
    ///
    /// Always settled, because the range must already have arrived: it is bounded by
    /// <see cref="AvailableLength"/> at the time of the call, and the bytes behind it cannot
    /// change afterwards. A caller streaming alongside a download therefore checks how far the
    /// origin has got, then opens ranges within it.
    ///
    /// Thread-safe and re-entrant - search calls this repeatedly off the UI thread while the
    /// document's own source is live.
    /// </summary>
    IByteSource OpenRange(long offset, long length);
}
