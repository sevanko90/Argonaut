using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;

namespace Argonaut.Features.Json.Indexing;

/// <summary>
/// Reads a whole document with <c>Utf8JsonReader</c> - comments skipped, trailing commas allowed,
/// as the tree reads - and keeps nothing but where it first fails. It is what gives the sparse
/// index, which counts brackets and never validates, the same failure messages and offsets the
/// token index reports, at no memory cost.
/// </summary>
internal static class JsonDocumentValidator
{
    /// <summary>How much is gathered past a piece boundary at a time, doubled for a token that
    /// is longer still.</summary>
    private const int InitialGatherBytes = 1024 * 1024;

    /// <summary>How the tree reads JSON, which every pass over it shares.</summary>
    internal static readonly JsonReaderOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Null when the document is valid JSON; otherwise why and where it is not. Waits for a
    /// source still arriving, so it runs on a background thread. The failure's
    /// <see cref="IndexFailure.ItemsIndexed"/> is the tokens read before it - what the view could
    /// show. A <paramref name="hashes"/> recorder is fed every token on the way, so content
    /// hashes cost no pass of their own.
    /// </summary>
    public static IndexFailure? FindFailure(IByteSource source, CancellationToken cancellationToken, JsonContentHashRecorder? hashes = null)
    {
        long offset = 0;
        long lastGoodEnd = -1;
        int gatherBytes = InitialGatherBytes;
        int tokens = 0;
        var state = new JsonReaderState(Options);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long available = source.AvailableLength;
                if (offset >= available)
                {
                    if (source.LengthSettled)
                        return null;

                    source.WaitForLength(offset + 1, cancellationToken);
                    continue;
                }

                // Parse over the source's own bytes where it serves them in one piece - a whole
                // mapping, the common case - resuming across windows through the reader state.
                // Only the end of settled data is a final block.
                var window = source.GetContiguousSpan(offset, (int)Math.Min(int.MaxValue, available - offset));
                byte[]? gathered = null;
                if (window.Length < available - offset && window.Length < gatherBytes)
                {
                    // A split source stops the span at a piece boundary, and the reader cannot
                    // resume inside a token cut there. Gather past the boundary instead.
                    int length = (int)Math.Min(available - offset, gatherBytes);
                    gathered = ArrayPool<byte>.Shared.Rent(length);
                    window = gathered.AsSpan(0, source.CopyTo(offset, gathered.AsSpan(0, length)));
                }

                try
                {
                    bool isFinalBlock = source.LengthSettled && offset + window.Length >= available;
                    var reader = new Utf8JsonReader(window, isFinalBlock, state);

                    while (reader.Read())
                    {
                        hashes?.Observe(ref reader, offset);
                        lastGoodEnd = offset + reader.BytesConsumed;
                        if ((++tokens & 0xFFFF) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                    }

                    long consumed = reader.BytesConsumed;
                    state = reader.CurrentState;

                    if (consumed == 0 && !isFinalBlock)
                    {
                        if (offset + window.Length < available && gatherBytes < int.MaxValue)
                        {
                            // One token longer than what was gathered: gather more.
                            gatherBytes = (int)Math.Min(int.MaxValue, (long)gatherBytes * 2);
                            continue;
                        }

                        if (!source.LengthSettled)
                        {
                            source.WaitForLength(available + 1, cancellationToken);
                            continue;
                        }

                        return new IndexFailure(
                            $"A single JSON token larger than this parse window ({window.Length} bytes) is not supported.",
                            offset, null, null, tokens);
                    }

                    offset += consumed;
                }
                finally
                {
                    if (gathered is not null)
                        ArrayPool<byte>.Shared.Return(gathered);
                }
            }
        }
        catch (JsonException ex)
        {
            long troubleAt = lastGoodEnd < 0 ? 0 : JsonFailureLocation.StartOfTroubleAfter(source, lastGoodEnd);
            return new IndexFailure(
                ex.Message,
                troubleAt,
                ex.LineNumber.HasValue ? ex.LineNumber.Value + 1 : null,
                ex.BytePositionInLine.HasValue ? ex.BytePositionInLine.Value + 1 : null,
                tokens);
        }
    }
}
