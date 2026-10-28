using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Xml.Indexing;

/// <summary>
/// A finished scan's structure, detached from the index that built it so it can be kept past
/// that session (see <see cref="KeptIndexes"/>) and bound again to a source over the same bytes
/// with <see cref="XmlSparseIndex.Reopen"/>. Holds no source. Carries the sibling counts with the
/// structure, since the checkpoints they sit beside are part of it.
/// </summary>
public sealed class XmlKeptStructure
{
    internal XmlKeptStructure(SparseContainerIndex structure, XmlSiblingCounts siblingCounts, long length)
    {
        Structure = structure;
        SiblingCounts = siblingCounts;
        Length = length;
    }

    /// <summary>The key a document's structure is kept under.</summary>
    public static object Key { get; } = typeof(XmlKeptStructure);

    internal SparseContainerIndex Structure { get; }

    internal XmlSiblingCounts SiblingCounts { get; }

    /// <summary>The length of the bytes it was built over.</summary>
    public long Length { get; }
}

/// <summary>What an <see cref="XmlSparseIndex"/>'s indexing task faults with when the document's
/// structure is broken; <see cref="XmlSparseIndex.Failure"/> has the details.</summary>
public sealed class XmlDocumentInvalidException(string message) : Exception(message);

/// <summary>
/// The XML tree's index: a <see cref="SparseContainerIndex"/> of the elements large enough to be
/// worth a record, built in the background in one pass by <see cref="XmlIndexScan"/>. Its size
/// depends on the file's size, not on how many nodes it holds - everything between its records is
/// re-parsed on demand. Only an element whose content holds an element, a comment or a processing
/// instruction is a container; one of text alone, however long, is a leaf and is never recorded.
///
/// Beside the structure it keeps <see cref="SiblingCounts"/>: how many siblings of each name stand
/// before each checkpoint, which is what the <c>k</c> of an XPath step <c>name[k]</c> is made of.
///
/// The one pass is also the validation, of structure only: a close tag that does not match, an
/// element still open at the end, a close tag with nothing to close, an unterminated construct, a
/// second root element, and text outside the root. The scan carries on past each, so the rest of
/// the file stays reachable - a stopped scan would leave it reachable only by reading sibling
/// after sibling from the last resume point - and the first one in document order is reported once
/// the index is complete, with its line and column.
/// </summary>
public sealed class XmlSparseIndex : ITreeIndex
{
    public const int DefaultPromotionBytes = 64 * 1024;
    public const int DefaultCheckpointBytes = 64 * 1024;

    private volatile bool allItemsPublished;
    private volatile IndexFailure? failure;

    private XmlSparseIndex(int promotionBytes, int checkpointBytes)
    {
        Structure = new SparseContainerIndex(promotionBytes, checkpointBytes);
        SiblingCounts = new XmlSiblingCounts();
    }

    private XmlSparseIndex(XmlKeptStructure kept)
    {
        Structure = kept.Structure;
        SiblingCounts = kept.SiblingCounts;
        allItemsPublished = true;
    }

    /// <summary>
    /// An index over <paramref name="source"/> built from the structure a finished scan of the
    /// same bytes left behind: complete at once, no scan. The caller vouches that the bytes are the
    /// same (a <see cref="ByteOriginVersion"/> match); the length is checked here as well, because
    /// a seek past the end of a shorter source would read out of bounds.
    /// </summary>
    public static XmlSparseIndex Reopen(IByteSource source, XmlKeptStructure kept)
    {
        if (source.AvailableLength != kept.Length || !source.LengthSettled)
            throw new ArgumentException("The structure was built over different bytes.", nameof(kept));

        return new XmlSparseIndex(kept);
    }

    /// <summary>
    /// This index's structure, detached so it can be kept past this session - or null unless the
    /// scan finished over a valid document. A cancelled scan covers only part of the bytes, and a
    /// failed one carries a failure the reopened index would not report.
    /// </summary>
    public XmlKeptStructure? DetachStructure() =>
        IndexingTask.IsCompletedSuccessfully && Failure is null && Structure.IsComplete
            ? new XmlKeptStructure(Structure, SiblingCounts, Structure.ScannedTo)
            : null;

    /// <summary>The recorded containers and checkpoints. <see cref="TreeContainer.FormatKind"/>
    /// is an <see cref="XmlNodeKind"/>: always <c>Element</c>.</summary>
    public SparseContainerIndex Structure { get; }

    /// <summary>The same-name counts that sit beside <see cref="Structure"/>'s checkpoints and
    /// closed containers.</summary>
    public XmlSiblingCounts SiblingCounts { get; }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public bool AllItemsPublished => allItemsPublished;

    /// <summary>Why the document is not well-formed: the first thing in it that broke the tree.
    /// Set before <see cref="AllItemsPublished"/>, and only once the index is complete.</summary>
    public IndexFailure? Failure => failure;

    /// <summary>Recorded containers so far.</summary>
    public int ItemCount => Structure.ContainerCount;

    public static XmlSparseIndex StartIndexing(IByteSource source, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        => StartIndexing(source, DefaultPromotionBytes, DefaultCheckpointBytes, progressReporter, cancellationToken);

    /// <summary>
    /// <see cref="Reopen"/> on a <paramref name="kept"/> structure when there is one, else a fresh
    /// scan - the factory a view hands its session.
    /// </summary>
    public static XmlSparseIndex StartIndexing(IByteSource source, XmlKeptStructure? kept, IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
        => kept is null ? StartIndexing(source, progressReporter, cancellationToken) : Reopen(source, kept);

    /// <summary>With explicit sizes - small ones let a test document of a few KB exercise
    /// promotion and checkpoints.</summary>
    public static XmlSparseIndex StartIndexing(IByteSource source, int promotionBytes, int checkpointBytes,
        IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var index = new XmlSparseIndex(promotionBytes, checkpointBytes);

        // No token on Task.Run, for the reason AppendLogIndexBase.StartScan gives: a token
        // already cancelled would skip the body, and with it the finally that publishes.
        index.IndexingTask = Task.Run(() => index.Run(source, progressReporter, cancellationToken));
        return index;
    }

    private void Run(IByteSource source, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        var scan = new XmlIndexScan(source, Structure, SiblingCounts, progressReporter, cancellationToken);
        try
        {
            scan.Run();

            // The index is complete; now say what was wrong with the document, if anything.
            if (scan.Failure is { } broken)
            {
                failure = broken;
                throw new XmlDocumentInvalidException(broken.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlDocumentInvalidException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = new IndexFailure(ex.Message, scan.Position, null, null, Structure.ContainerCount);
            throw;
        }
        finally
        {
            allItemsPublished = true;
        }
    }
}
