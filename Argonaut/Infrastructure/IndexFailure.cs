namespace Argonaut.Infrastructure;

/// <summary>Why a background scan stopped early. Never produced for cancellation.</summary>
/// <param name="Message">Human-readable description of what went wrong.</param>
/// <param name="ByteOffset">Best-effort absolute offset in the file, or null if unknown.</param>
/// <param name="Line">1-based line number, when the indexer can supply it.</param>
/// <param name="Column">1-based column number, when the indexer can supply it.</param>
/// <param name="ItemsIndexed">Records published before the failure - drives the zero-progress rule.</param>
public sealed record IndexFailure(
    string Message,
    long? ByteOffset,
    long? Line,
    long? Column,
    int ItemsIndexed);
