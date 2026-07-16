using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

public enum JsonTokenKind
{
    StartObject,
    EndObject,
    StartArray,
    EndArray,
    String,
    Number,
    True,
    False,
    Null
}

/// <summary>
/// A structural (non-decoding) record of one JSON value/container token.
/// Text is never materialized here - callers re-read (Offset, Length) from the
/// backing MMapFile on demand, the same way FileOffsetIndex/FileLineSpan works for NDJSON.
/// </summary>
public record struct JsonTokenInfo(
    JsonTokenKind Kind,
    int Depth,
    long Offset,
    int Length,
    int ParentIndex,
    int EndIndex,
    long NameOffset,
    int NameLength);

/// <summary>
/// Background structural indexer for a large JSON document over a memory-mapped file.
/// Walks the file once with a streaming Utf8JsonReader and records only fixed-size
/// structural info per token (kind/depth/offset/length/parent/matching-end/name-span) -
/// no token text is ever decoded or retained during indexing, which is what let the
/// previous JsonIndexer blow up memory on large files.
/// </summary>
public sealed class JsonStructureIndex
{
    private const int ChunkSize = 64 * 1024;

    private readonly Lock sync = new();
    private readonly List<JsonTokenInfo> tokens = new(4096);

    private TaskCompletionSource<bool>? countReady;
    private int countReadyTarget;
    private bool complete;
    private Exception? failure;

    private JsonStructureIndex()
    {
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Number of tokens indexed so far (may grow until <see cref="IsComplete"/> is true).
    /// </summary>
    public int TokenCount
    {
        get
        {
            lock (sync)
                return tokens.Count;
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (sync)
                return complete;
        }
    }

    public JsonTokenInfo GetToken(int index)
    {
        lock (sync)
            return tokens[index];
    }

    /// <summary>
    /// Waits (asynchronously) until at least <paramref name="targetCount"/> tokens are indexed,
    /// or indexing completes with fewer tokens than that.
    /// </summary>
    public Task WaitForTokenCountAsync(int targetCount)
    {
        lock (sync)
        {
            if (tokens.Count >= targetCount || complete)
                return Task.CompletedTask;

            if (countReady is null || countReady.Task.IsCompleted || targetCount > countReadyTarget)
            {
                countReadyTarget = targetCount;
                countReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return countReady.Task;
        }
    }

    /// <summary>
    /// Waits until the token at <paramref name="tokenIndex"/> has been indexed (i.e. TokenCount &gt; tokenIndex),
    /// or indexing completes. Used when expanding into a region of the document not yet indexed.
    /// </summary>
    public Task WaitForTokenIndexedAsync(int tokenIndex) => WaitForTokenCountAsync(tokenIndex + 1);

    public static JsonStructureIndex StartIndexing(MMapFile file, IProgressReporter? progressReporter = null)
    {
        var index = new JsonStructureIndex();
        index.IndexingTask = Task.Run(() => index.Run(file, progressReporter));
        return index;
    }

    private void Run(MMapFile file, IProgressReporter? progressReporter)
    {
        try
        {
            Build(file, progressReporter);
        }
        catch (Exception ex)
        {
            lock (sync)
                failure = ex;
            throw;
        }
        finally
        {
            MarkComplete();
        }
    }

    private void Build(MMapFile file, IProgressReporter? progressReporter)
    {
        long offset = 0;
        long length = file.Length;

        var state = new JsonReaderState(new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var openContainers = new Stack<int>();
        long pendingNameOffset = -1;
        int pendingNameLength = -1;

        // Grows when a single token (e.g. a long embedded string) doesn't fit in one
        // chunk, so we never get stuck re-reading the same window with zero progress.
        int chunkSize = ChunkSize;

        while (offset < length)
        {
            int size = (int)Math.Min(chunkSize, length - offset);
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            bool isFinalBlock = offset + size >= length;
            long consumed;
            try
            {
                int bytesRead = file.Read(offset, buffer, size);
                if (bytesRead == 0)
                    break;

                var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesRead), isFinalBlock, state);

                while (reader.Read())
                {
                    var tokenType = reader.TokenType;

                    // TokenStartIndex points at the opening quote for String/PropertyName;
                    // ValueSpan/ValueSequence already exclude the quotes, so skip past it to
                    // keep (Offset, Length) pointing exactly at the decodable content bytes.
                    bool isQuoted = tokenType is JsonTokenType.String or JsonTokenType.PropertyName;
                    long rawTokenOffset = offset + reader.TokenStartIndex + (isQuoted ? 1 : 0);
                    int rawTokenLength = reader.HasValueSequence
                        ? (int)reader.ValueSequence.Length
                        : reader.ValueSpan.Length;

                    if (tokenType == JsonTokenType.PropertyName)
                    {
                        pendingNameOffset = rawTokenOffset;
                        pendingNameLength = rawTokenLength;
                        continue;
                    }

                    var kind = Map(tokenType);
                    int parentIndex = openContainers.Count > 0 ? openContainers.Peek() : -1;
                    int depth = openContainers.Count;

                    int tokenIndex;
                    lock (sync)
                    {
                        tokenIndex = tokens.Count;
                        tokens.Add(new JsonTokenInfo(
                            kind, depth, rawTokenOffset, rawTokenLength,
                            parentIndex, -1,
                            pendingNameOffset, pendingNameLength));
                    }

                    pendingNameOffset = -1;
                    pendingNameLength = -1;

                    if (tokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        openContainers.Push(tokenIndex);
                    }
                    else if (tokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        int startIndex = openContainers.Pop();
                        lock (sync)
                        {
                            var start = tokens[startIndex];
                            start.EndIndex = tokenIndex;
                            tokens[startIndex] = start;
                        }
                    }

                    NotifyCountReady();
                }

                consumed = reader.BytesConsumed;
                state = reader.CurrentState;

                if (consumed == 0 && !isFinalBlock)
                {
                    // No complete token fit in this window at all - the current token is
                    // larger than chunkSize (e.g. a huge embedded string). Grow and retry
                    // at the same offset instead of spinning with no progress.
                    chunkSize = checked(chunkSize * 2);
                    continue;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            offset += consumed;
            chunkSize = ChunkSize;
            progressReporter?.Report(offset, length);
        }

        progressReporter?.Report(length, length);
    }

    private void NotifyCountReady()
    {
        TaskCompletionSource<bool>? waiter = null;
        lock (sync)
        {
            if (countReady is not null && !countReady.Task.IsCompleted && countReadyTarget > 0 &&
                tokens.Count >= countReadyTarget)
                waiter = countReady;
        }

        waiter?.TrySetResult(true);
    }

    private void MarkComplete()
    {
        TaskCompletionSource<bool>? waiter;
        lock (sync)
        {
            complete = true;
            waiter = countReady;
        }

        waiter?.TrySetResult(true);
    }

    private static JsonTokenKind Map(JsonTokenType t) => t switch
    {
        JsonTokenType.StartObject => JsonTokenKind.StartObject,
        JsonTokenType.EndObject => JsonTokenKind.EndObject,
        JsonTokenType.StartArray => JsonTokenKind.StartArray,
        JsonTokenType.EndArray => JsonTokenKind.EndArray,
        JsonTokenType.String => JsonTokenKind.String,
        JsonTokenType.Number => JsonTokenKind.Number,
        JsonTokenType.True => JsonTokenKind.True,
        JsonTokenType.False => JsonTokenKind.False,
        JsonTokenType.Null => JsonTokenKind.Null,
        _ => throw new NotSupportedException($"Unexpected top-level token type: {t}")
    };
}
