using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;

namespace Argonaut.Features.Json.Indexing;

/// <summary>
/// The JSON tree's index: a <see cref="SparseContainerIndex"/> of the containers large enough to
/// be worth a record, built in the background from <see cref="JsonBlockClassifier"/> masks. Its
/// size depends on the file's size, not on how many tokens it holds - everything between its
/// records is re-parsed on demand.
///
/// The scan turns masks into the builder's events: every bracket opens or closes, and a comma
/// becomes a separator only once something follows it. A comma that the closing bracket follows
/// is a trailing comma (JSONC, which the tree reads) and separates nothing, so each one is held
/// until the next bracket or comma shows whether content came between. The same content test
/// tells an empty container from one with a single child.
///
/// It does not validate: brackets are counted, not matched. A document that does not close
/// fails the scan; a subtler error is left to the parse that reads those bytes.
/// </summary>
public sealed class JsonSparseIndex : IBackgroundIndex
{
    public const int DefaultPromotionBytes = 64 * 1024;
    public const int DefaultCheckpointBytes = 64 * 1024;

    /// <summary>Bytes asked of the source per turn of the scan loop. Bounds how often progress,
    /// cancellation and the published scan position are updated.</summary>
    private const int ReadStride = 256 * 1024;

    private const long ProgressReportStride = 4 * 1024 * 1024;

    private readonly SparseContainerIndexBuilder builder;
    private volatile bool allItemsPublished;
    private volatile IndexFailure? failure;

    // Scan state carried from block to block - see ProcessBlock.
    private long pendingSeparator = -1;
    private bool contentSinceMark;
    private bool[] frameHasContent = new bool[64];
    private int depth;

    private JsonSparseIndex(int promotionBytes, int checkpointBytes)
    {
        Structure = new SparseContainerIndex(promotionBytes, checkpointBytes);
        builder = new SparseContainerIndexBuilder(Structure);
    }

    /// <summary>The recorded containers and checkpoints. <see cref="TreeContainer.FormatKind"/>
    /// is a <see cref="JsonTokenKind"/>: <c>StartObject</c> or <c>StartArray</c>.</summary>
    public SparseContainerIndex Structure { get; }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public bool AllItemsPublished => allItemsPublished;

    public IndexFailure? Failure => failure;

    /// <summary>Recorded containers so far.</summary>
    public int ItemCount => Structure.ContainerCount;

    public static JsonSparseIndex StartIndexing(IByteSource source, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        => StartIndexing(source, DefaultPromotionBytes, DefaultCheckpointBytes, progressReporter, cancellationToken);

    /// <summary>With explicit sizes - small ones let a test document of a few KB exercise
    /// promotion and checkpoints.</summary>
    public static JsonSparseIndex StartIndexing(IByteSource source, int promotionBytes, int checkpointBytes,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var index = new JsonSparseIndex(promotionBytes, checkpointBytes);

        // No token on Task.Run, for the reason AppendLogIndexBase.StartScan gives: a token
        // already cancelled would skip the body, and with it the finally that publishes.
        index.IndexingTask = Task.Run(() => index.Run(source, progressReporter, cancellationToken));
        return index;
    }

    private void Run(IByteSource source, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        long offset = 0;
        try
        {
            Scan(source, progressReporter, cancellationToken, ref offset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = new IndexFailure(ex.Message, offset, null, null, Structure.ContainerCount);
            throw;
        }
        finally
        {
            allItemsPublished = true;
        }
    }

    private void Scan(IByteSource source, IProgressReporter? progressReporter, CancellationToken cancellationToken, ref long offset)
    {
        var classifier = default(JsonBlockClassifier);
        Span<byte> gathered = stackalloc byte[JsonBlockClassifier.BlockSize];
        long nextReport = ProgressReportStride;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read every turn, never snapshotted: over a streamed source it is only the end
            // so far (see IByteSource.AvailableLength).
            long available = source.AvailableLength;
            if (offset >= available)
            {
                if (source.LengthSettled)
                    break;

                builder.Advance(offset);
                source.WaitForLength(offset + 1, cancellationToken);
                continue;
            }

            var span = source.GetContiguousSpan(offset, (int)Math.Min(ReadStride, available - offset));
            int whole = span.Length - (span.Length % JsonBlockClassifier.BlockSize);
            for (int i = 0; i < whole; i += JsonBlockClassifier.BlockSize)
            {
                var block = span.Slice(i, JsonBlockClassifier.BlockSize);
                classifier.Classify(block, out var masks);
                ProcessBlock(block, masks, offset + i);
            }

            offset += whole;

            if (whole < span.Length)
            {
                // Less than a block left in this span: the source's buffer ends here and the
                // next continues, or this is the end of what has arrived.
                long remaining = available - offset;
                if (remaining >= JsonBlockClassifier.BlockSize)
                {
                    source.CopyTo(offset, gathered);
                    classifier.Classify(gathered, out var masks);
                    ProcessBlock(gathered, masks, offset);
                    offset += JsonBlockClassifier.BlockSize;
                }
                else if (source.LengthSettled)
                {
                    // The document's last partial block, padded with whitespace, which opens
                    // and closes nothing.
                    int copied = source.CopyTo(offset, gathered);
                    gathered.Slice(copied).Fill((byte)' ');
                    classifier.Classify(gathered, out var masks);
                    ProcessBlock(gathered, masks, offset);
                    offset += copied;
                }
                else
                {
                    // Classifying a partial block would carry a guess about its missing bytes
                    // into the next one, so wait until a whole block has arrived.
                    builder.Advance(offset);
                    source.WaitForLength(offset + JsonBlockClassifier.BlockSize, cancellationToken);
                    continue;
                }
            }

            builder.Advance(offset);
            if (offset >= nextReport)
            {
                progressReporter?.Report("Indexing", offset, available);
                while (nextReport <= offset)
                    nextReport += ProgressReportStride;
            }
        }

        if (depth != 0)
            throw new InvalidDataException($"The document ends with {depth} container(s) still open.");

        builder.Complete(offset);
        progressReporter?.Report("Indexing", offset, offset);
    }

    /// <summary>
    /// Turns one block's brackets and commas into builder events, in order. Between two of them,
    /// the content mask says whether anything else came - which is what decides whether a held
    /// comma separates two children or trails the last one, and whether a closing container was
    /// empty.
    /// </summary>
    private void ProcessBlock(ReadOnlySpan<byte> block, in JsonBlockMasks masks, long blockOffset)
    {
        ulong structural = masks.Open | masks.Close | masks.Comma;
        int previous = -1;

        while (structural != 0)
        {
            int bit = BitOperations.TrailingZeroCount(structural);
            structural &= structural - 1;

            if ((masks.Content & BitsBetween(previous, bit)) != 0)
                contentSinceMark = true;
            previous = bit;

            if (contentSinceMark)
                MarkContent();

            long position = blockOffset + bit;
            ulong mask = 1UL << bit;

            if ((masks.Open & mask) != 0)
            {
                MarkContent(); // the container is itself content in its parent
                byte kind = block[bit] == (byte)'{' ? (byte)JsonTokenKind.StartObject : (byte)JsonTokenKind.StartArray;
                builder.Open(position, kind);
                if (depth == frameHasContent.Length)
                    Array.Resize(ref frameHasContent, frameHasContent.Length * 2);
                frameHasContent[depth++] = false;
                contentSinceMark = false;
            }
            else if ((masks.Close & mask) != 0)
            {
                if (depth == 0)
                    throw new InvalidDataException($"A closing bracket at byte {position} has nothing to close.");

                pendingSeparator = -1; // nothing followed it: a trailing comma
                bool isEmpty = !frameHasContent[--depth];
                builder.Close(position + 1, isEmpty);
                contentSinceMark = true; // the closed container is content in its parent
            }
            else
            {
                // A comma with no content since the last one (",,") is not JSON; the held one
                // is kept and this one dropped, and validation reports the error.
                if (pendingSeparator < 0)
                    pendingSeparator = position + 1;
                contentSinceMark = false;
            }
        }

        if ((masks.Content & BitsAbove(previous)) != 0)
            contentSinceMark = true;
    }

    /// <summary>Content has appeared in the innermost container since its last mark, so a held
    /// comma does separate two children.</summary>
    private void MarkContent()
    {
        if (depth > 0)
            frameHasContent[depth - 1] = true;

        if (pendingSeparator >= 0)
        {
            builder.Separator(pendingSeparator);
            pendingSeparator = -1;
        }

        contentSinceMark = false;
    }

    /// <summary>Bits strictly between <paramref name="low"/> (-1 for none) and
    /// <paramref name="high"/>.</summary>
    private static ulong BitsBetween(int low, int high)
    {
        ulong below = high == 0 ? 0 : (1UL << high) - 1;
        return below & ~ThroughBit(low);
    }

    /// <summary>Bits strictly above <paramref name="low"/> (-1 for all of them).</summary>
    private static ulong BitsAbove(int low) => ~ThroughBit(low);

    private static ulong ThroughBit(int bit) => bit < 0 ? 0 : bit == 63 ? ulong.MaxValue : (2UL << bit) - 1;
}
