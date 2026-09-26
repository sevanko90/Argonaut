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
/// A finished scan's structure, detached from the index that built it so it can be kept past
/// that session (see <see cref="KeptIndexes"/>) and bound again to a source over the same bytes
/// with <see cref="JsonSparseIndex.Reopen"/>. Holds no source.
/// </summary>
public sealed class JsonKeptStructure
{
    internal JsonKeptStructure(SparseContainerIndex structure, long length)
    {
        Structure = structure;
        Length = length;
    }

    /// <summary>The key a document's structure is kept under.</summary>
    public static object Key { get; } = typeof(JsonKeptStructure);

    internal SparseContainerIndex Structure { get; }

    /// <summary>The length of the bytes it was built over.</summary>
    public long Length { get; }
}

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
/// It does not validate: brackets are counted, not matched. Validation runs beside it on its
/// own thread (<see cref="JsonDocumentValidator"/>), holding no memory, and its failure - with the
/// reader's message, line and column - is the one reported. The scan itself carries on through
/// what it cannot make sense of: a truncated document's open containers end where the data does,
/// and a stray closing bracket is skipped. Either still fails the scan once it is done, and that
/// failure stands only if validation somehow passed.
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
    private volatile IndexFailure? structureFailure;
    private volatile IndexFailure? validationFailure;

    // Scan state carried from block to block - see ProcessBlock.
    private long pendingSeparator = -1;
    private bool contentSinceMark;
    private bool[] frameHasContent = new bool[64];
    private int depth;
    private long strayClose = -1;

    private JsonSparseIndex(IByteSource source, int promotionBytes, int checkpointBytes, bool withContentHashes)
    {
        Structure = new SparseContainerIndex(promotionBytes, checkpointBytes);
        builder = new SparseContainerIndexBuilder(Structure);
        if (withContentHashes)
            ContentHashes = new JsonContentHashes(source, promotionBytes);
    }

    private JsonSparseIndex(SparseContainerIndex structure)
    {
        Structure = structure;
        builder = new SparseContainerIndexBuilder(structure);
        allItemsPublished = true;
    }

    /// <summary>
    /// An index over <paramref name="source"/> built from the structure a finished scan of the
    /// same bytes left behind: complete at once, no scan. The caller vouches that the bytes are the
    /// same (a <see cref="ByteOriginVersion"/> match); the length is checked here as well, because
    /// a seek past the end of a shorter source would read out of bounds.
    /// </summary>
    public static JsonSparseIndex Reopen(IByteSource source, JsonKeptStructure kept)
    {
        if (source.AvailableLength != kept.Length || !source.LengthSettled)
            throw new ArgumentException("The structure was built over different bytes.", nameof(kept));

        return new JsonSparseIndex(kept.Structure);
    }

    /// <summary>
    /// This index's structure, detached so it can be kept past this session - or null unless the
    /// scan finished over a valid document. A cancelled scan covers only part of the bytes, and a
    /// failed one carries a failure the reopened index would not report.
    /// </summary>
    public JsonKeptStructure? DetachStructure() =>
        IndexingTask.IsCompletedSuccessfully && Failure is null && Structure.IsComplete
            ? new JsonKeptStructure(Structure, Structure.ScannedTo)
            : null;

    /// <summary>The recorded containers and checkpoints. <see cref="TreeContainer.FormatKind"/>
    /// is a <see cref="JsonTokenKind"/>: <c>StartObject</c> or <c>StartArray</c>.</summary>
    public SparseContainerIndex Structure { get; }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public bool AllItemsPublished => allItemsPublished;

    /// <summary>Why the document failed: validation's answer when it has one. Set before
    /// <see cref="AllItemsPublished"/>, which waits for both passes.</summary>
    public IndexFailure? Failure => validationFailure ?? structureFailure;

    /// <summary>Every value's content hash, for a diff; null unless asked for. Recorded by the
    /// validation pass for the same containers the structure records.</summary>
    public JsonContentHashes? ContentHashes { get; }

    /// <summary>Recorded containers so far.</summary>
    public int ItemCount => Structure.ContainerCount;

    public static JsonSparseIndex StartIndexing(IByteSource source, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        => StartIndexing(source, DefaultPromotionBytes, DefaultCheckpointBytes, progressReporter, cancellationToken);

    /// <summary>With explicit sizes - small ones let a test document of a few KB exercise
    /// promotion and checkpoints.</summary>
    public static JsonSparseIndex StartIndexing(IByteSource source, int promotionBytes, int checkpointBytes,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        => Start(source, promotionBytes, checkpointBytes, withContentHashes: false, progressReporter, cancellationToken);

    /// <summary>Also records <see cref="ContentHashes"/> - what a diff compares by.</summary>
    public static JsonSparseIndex StartIndexingWithContentHashes(IByteSource source, IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
        => Start(source, DefaultPromotionBytes, DefaultCheckpointBytes, withContentHashes: true, progressReporter, cancellationToken);

    /// <inheritdoc cref="StartIndexingWithContentHashes(IByteSource, IProgressReporter?, CancellationToken)"/>
    public static JsonSparseIndex StartIndexingWithContentHashes(IByteSource source, int promotionBytes, int checkpointBytes,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        => Start(source, promotionBytes, checkpointBytes, withContentHashes: true, progressReporter, cancellationToken);

    private static JsonSparseIndex Start(IByteSource source, int promotionBytes, int checkpointBytes, bool withContentHashes,
        IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        var index = new JsonSparseIndex(source, promotionBytes, checkpointBytes, withContentHashes);

        // No token on Task.Run, for the reason AppendLogIndexBase.StartScan gives: a token
        // already cancelled would skip the body, and with it the finally that publishes.
        index.IndexingTask = Task.Run(() => index.RunBothPasses(source, progressReporter, cancellationToken));
        return index;
    }

    /// <summary>
    /// The structural scan and validation side by side, each running to its own end. A
    /// validation failure does not stop the scan: the tree reads past a corrupt stretch the way a
    /// reader of the raw file would, and it can only jump to what the index covers - a scan
    /// stopped at the first error would leave the rest of a multi-GB file reachable only by
    /// reading sibling after sibling from the last resume point, on the UI thread.
    /// </summary>
    private async Task RunBothPasses(IByteSource source, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var structure = Task.Run(() => RunStructure(source, progressReporter, stopping.Token));
        var validation = Task.Run(() =>
        {
            if (JsonDocumentValidator.FindFailure(source, stopping.Token, ContentHashes?.CreateRecorder()) is { } found)
            {
                validationFailure = found;
                throw new JsonDocumentInvalidException(found.Message);
            }
        });

        try
        {
            await Task.WhenAll(structure, validation);
        }
        finally
        {
            allItemsPublished = true;
        }
    }

    private void RunStructure(IByteSource source, IProgressReporter? progressReporter, CancellationToken cancellationToken)
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
            structureFailure = new IndexFailure(ex.Message, offset, null, null, Structure.ContainerCount);
            throw;
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

        // A truncated document ends inside containers. They end where the data does - so the index
        // is complete, and nothing has to scan to the end of the file to find where they stop -
        // and the scan still fails, so the document is reported as broken.
        int unclosed = depth;
        while (depth > 0)
            builder.Close(offset, isEmpty: !frameHasContent[--depth]);

        builder.Complete(offset);
        progressReporter?.Report("Indexing", offset, offset);

        if (unclosed > 0)
            throw new InvalidDataException($"The document ends with {unclosed} container(s) still open.");
        if (strayClose >= 0)
            throw new InvalidDataException($"A closing bracket at byte {strayClose} has nothing to close.");
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
                {
                    // Nothing to close: not JSON, and validation says so. Skip it and carry on, so
                    // one stray bracket does not end the index for the rest of the file.
                    if (strayClose < 0)
                        strayClose = position;
                    continue;
                }

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
