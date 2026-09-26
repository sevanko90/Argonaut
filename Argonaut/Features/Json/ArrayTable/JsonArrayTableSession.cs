using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Progress;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents;

namespace Argonaut.Features.Json.ArrayTable;

/// <summary>
/// The session behind one array-as-table document: a sparse-index session over the array's own
/// byte range, and the readers the table reads it through.
///
/// The mapping is the ARRAY, not the file: <c>[</c>…<c>]</c> is a valid JSON document on its
/// own, so the table opens an independent sub-range and shares nothing with the JSON document it
/// was opened from - which is required, not merely tidy, because the shell disposes the outgoing
/// document before publishing this one. Offsets inside it are relative to the array's start;
/// <see cref="ArrayOffset"/> turns one back into a file offset.
///
/// Idempotent, same contract as its siblings. Not thread-safe: create and dispose from the UI
/// thread.
/// </summary>
public sealed class JsonArrayTableSession : IDocumentSession
{
    private JsonArrayTableSession(IByteOrigin origin, long arrayOffset, IndexedSourceSession<JsonSparseIndex> inner)
    {
        Origin = origin;
        ArrayOffset = arrayOffset;
        Inner = inner;
        Reader = new JsonTreeReader(inner.Bytes);
        Text = new JsonTreeText(inner.Bytes, inner.Index.Structure, Reader);
        Elements = new JsonArrayElements(inner.Index, Reader, Text);
    }

    /// <summary>Where the array's bytes came from.</summary>
    public IByteOrigin Origin { get; }

    /// <summary>The array's first byte in <see cref="Origin"/>.</summary>
    public long ArrayOffset { get; }

    /// <summary>The sub-range mapping and its sparse index.</summary>
    public IndexedSourceSession<JsonSparseIndex> Inner { get; }

    public JsonTreeReader Reader { get; }

    /// <summary>Cell text and container summaries, the same the JSON tree shows.</summary>
    public JsonTreeText Text { get; }

    /// <summary>Ordinal addressing over the array's elements - the table's row source.</summary>
    public JsonArrayElements Elements { get; }

    public CancellationToken TearingDown => Inner.TearingDown;

    public Task IndexingTask => Inner.IndexingTask;

    public IndexFailure? Failure => Inner.Failure;

    /// <summary>
    /// Takes <paramref name="length"/> bytes of <paramref name="origin"/> starting at
    /// <paramref name="offset"/> - which must be exactly the array's <c>[</c>…<c>]</c> range -
    /// and indexes it as a JSON document in its own right.
    /// </summary>
    public static JsonArrayTableSession Start(IByteOrigin origin, long offset, long length, IProgressReporter? progressReporter = null)
    {
        var inner = IndexedSourceSession<JsonSparseIndex>.Start(
            origin.OpenRange(offset, length), JsonSparseIndex.StartIndexing, progressReporter);
        return new JsonArrayTableSession(origin, offset, inner);
    }

    public void RequestStop() => Inner.RequestStop();

    public void Dispose() => Inner.Dispose();
}
