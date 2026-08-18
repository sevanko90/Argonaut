namespace Argonaut.Features.Json;

/// <summary>
/// Options for <see cref="JsonStructureIndex.StartIndexing(Argonaut.Infrastructure.MMapFile, JsonIndexOptions, Argonaut.Infrastructure.IProgressReporter?, System.Threading.CancellationToken)"/>.
/// </summary>
public readonly struct JsonIndexOptions
{
    /// <summary>
    /// When set, the indexer additionally computes a 64-bit content hash per token (see
    /// <see cref="JsonContentHasher"/> for the hashing rules and
    /// <see cref="JsonStructureIndex.GetContentHash"/> for access). Costs 8 bytes/token of
    /// extra memory and the per-scalar hash work; off (the default), the hash log is never
    /// allocated and the only cost is one field null-check per token in the parse loop.
    /// Needed by the JSON diff, which compares subtrees by Merkle hash.
    /// </summary>
    public bool ComputeContentHashes { get; init; }
}
