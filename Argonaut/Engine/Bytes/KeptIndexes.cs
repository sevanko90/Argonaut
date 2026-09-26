using System.Collections.Generic;
using System.Threading;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// Indexes derived from an origin's bytes, kept for as long as the origin is open so a view that
/// is closed and reopened over the same input - a switch to the text view and back - finds its index
/// already built instead of scanning again. Lives on the origin (<see cref="IByteOrigin.KeptIndexes"/>)
/// because that is the one object whose lifetime is the open input, across view swaps.
///
/// What goes in must hold no source: a session releases its source when the view closes, so a
/// kept index is the records alone, and the next session binds them to a source of its own.
/// Each entry carries the <see cref="ByteOriginVersion"/> it was built at, and one that no longer
/// matches the origin is dropped rather than returned.
///
/// Only worth it for an index that is small next to what it saves: the raw view's row anchors
/// (16 bytes per 64 rows) and the JSON view's sparse structure (under 1 MB per GB) qualify; a
/// per-line offset index (CSV, NDJSON: about 16 bytes a line) is left to be scanned again.
/// </summary>
public sealed class KeptIndexes
{
    private readonly Lock sync = new();
    private readonly Dictionary<object, (ByteOriginVersion Version, object Index)> entries = new();

    /// <summary>The index kept under <paramref name="key"/>, if there is one and it was built
    /// over the bytes the origin holds now (<paramref name="current"/>).</summary>
    public bool TryGet<TIndex>(object key, ByteOriginVersion current, out TIndex index) where TIndex : class
    {
        lock (this.sync)
        {
            if (this.entries.TryGetValue(key, out var entry))
            {
                if (entry.Version == current && entry.Index is TIndex kept)
                {
                    index = kept;
                    return true;
                }

                this.entries.Remove(key);
            }
        }

        index = null!;
        return false;
    }

    /// <summary>Keeps <paramref name="index"/>, built over the origin at
    /// <paramref name="version"/>, replacing whatever was kept under <paramref name="key"/>. Anything
    /// kept at another version goes: it describes bytes the origin no longer holds.</summary>
    public void Keep(object key, ByteOriginVersion version, object index)
    {
        lock (this.sync)
        {
            List<object>? stale = null;
            foreach (var (existingKey, entry) in this.entries)
            {
                if (entry.Version != version)
                    (stale ??= []).Add(existingKey);
            }

            if (stale is not null)
            {
                foreach (var staleKey in stale)
                    this.entries.Remove(staleKey);
            }

            this.entries[key] = (version, index);
        }
    }

    /// <summary>Drops everything, for an origin being disposed.</summary>
    public void Clear()
    {
        lock (this.sync)
            this.entries.Clear();
    }
}
