using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Json.Indexing;

/// <summary>
/// The content hash of any value in a document (see <see cref="JsonContentHasher"/> for what it
/// covers), on the same budget as the sparse index: the validation pass records the hash of every
/// container at least <see cref="RecordBytes"/> long, and anything smaller is hashed from its own
/// bytes when asked. Asking for a small value costs one read of at most that many bytes; asking
/// for a large one costs a lookup.
///
/// The recorded hashes are complete only once the index's scan is - read them after
/// <see cref="JsonSparseIndex.IndexingTask"/>. <see cref="Hash"/> reuses one scratch parser, so
/// one thread at a time; the diff's worker is the only caller.
/// </summary>
public sealed class JsonContentHashes
{
    private readonly IByteSource bytes;
    private readonly JsonContentHashRecorder scratch = new(recorded: null, recordBytes: long.MaxValue);
    private Dictionary<long, ulong>? recorded = new();

    internal JsonContentHashes(IByteSource bytes, long recordBytes)
    {
        this.bytes = bytes;
        RecordBytes = recordBytes;
    }

    /// <summary>Containers at least this long have their hash recorded.</summary>
    public long RecordBytes { get; }

    /// <summary>Containers recorded.</summary>
    public int RecordedCount => recorded?.Count ?? 0;

    /// <summary>The recorder the validation pass feeds.</summary>
    internal JsonContentHashRecorder CreateRecorder() => new(recorded, RecordBytes);

    /// <summary>
    /// The hash of the value occupying [<paramref name="start"/>, <paramref name="end"/>): a
    /// recorded container's from the record, anything else by parsing its bytes.
    /// </summary>
    public ulong Hash(long start, long end)
    {
        var known = Volatile.Read(ref recorded) ?? throw new InvalidOperationException("Content hashes have been released.");
        if (known.TryGetValue(start, out ulong hash))
            return hash;

        // Smaller than the recording threshold, or a scalar - which validation bounds to what
        // one parse window can hold.
        var span = bytes.RequireContiguous(start, (int)Math.Min(int.MaxValue, end - start));
        var reader = new Utf8JsonReader(span, isFinalBlock: true, new JsonReaderState(JsonDocumentValidator.Options));
        scratch.Reset();
        while (reader.Read())
            scratch.Observe(ref reader, start);
        return scratch.TopLevelHash;
    }

    /// <summary>Drops the recorded hashes once their one consumer is done with them.</summary>
    public void Release() => Volatile.Write(ref recorded, null);
}
