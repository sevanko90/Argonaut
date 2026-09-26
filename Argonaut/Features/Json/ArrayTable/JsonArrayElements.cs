using System;
using System.Threading.Tasks;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;

namespace Argonaut.Features.Json.ArrayTable;

/// <summary>
/// The elements of one JSON array, addressed by ordinal, so a table can serve row <c>i</c>
/// without walking the array from the start. The array is the whole document the table indexed
/// (its own <c>[</c>…<c>]</c> range), so there is nothing to derive: element <c>i</c> is found
/// from the sparse index's nearest resume point before it and read from the bytes.
///
/// While the array is still being indexed it counts only the elements known to have ended - those
/// before its latest checkpoint - so a row that appears never changes afterwards.
///
/// Remembers the last run of elements it read, so a screen of consecutive rows reads the array
/// once rather than once per row. UI-thread only, like the table that uses it.
/// </summary>
public sealed class JsonArrayElements
{
    /// <summary>Elements kept from the last read, enough for a screen of rows and some slack
    /// either side.</summary>
    private const int CacheSize = 1024;

    private readonly JsonSparseIndex index;
    private readonly JsonTreeReader reader;
    private readonly JsonTreeText text;

    // A ring: element e sits at (e % CacheSize) for e in [cachedFirst, cachedFirst + cachedCount).
    private readonly TreeNode[] cached = new TreeNode[CacheSize];
    private long cachedFirst;
    private int cachedCount;

    private TreeNode? array;
    private long smallArrayCount = -1;

    public JsonArrayElements(JsonSparseIndex index, JsonTreeReader reader, JsonTreeText text)
    {
        this.index = index;
        this.reader = reader;
        this.text = text;
    }

    /// <summary>True once the scan has stopped: the count will not grow again.</summary>
    public bool AllItemsPublished => index.AllItemsPublished;

    /// <summary>Completes when the array's scan does.</summary>
    public Task IndexingTask => index.IndexingTask;

    /// <summary>The array's own node, once its first byte has arrived; null for a document that is
    /// not an array.</summary>
    public TreeNode? Array
    {
        get
        {
            if (array is null)
            {
                long position = 0;
                if (reader.TryReadChild(JsonTreeReader.Document, ref position, out var root, out _)
                    && root.FormatKind == (byte)JsonTokenKind.StartArray)
                    array = root;
            }

            return array;
        }
    }

    /// <summary>Elements addressable so far. May grow until <see cref="AllItemsPublished"/>.</summary>
    public int ElementCount
    {
        get
        {
            if (Array is not { } node)
                return 0;

            var structure = index.Structure;
            int record = structure.FindContainerStartingAt(node.ValueStart);
            if (record >= 0)
                return (int)Math.Min(int.MaxValue, structure.KnownChildCount(record));

            // Not recorded: smaller than the promotion size, so read through once it has all
            // arrived, and remembered.
            if (!structure.IsComplete && !index.AllItemsPublished)
                return 0;

            if (smallArrayCount < 0)
                smallArrayCount = text.ChildCount(node.ValueStart);
            return (int)Math.Max(0, smallArrayCount);
        }
    }

    /// <summary>Waits until at least <paramref name="targetCount"/> elements are addressable, or
    /// the scan has stopped.</summary>
    public async Task WaitForElementCountAsync(int targetCount)
    {
        while (ElementCount < targetCount && !AllItemsPublished)
            await Task.Delay(20);
    }

    /// <summary>Element <paramref name="ordinal"/>, which must be below
    /// <see cref="ElementCount"/>.</summary>
    public TreeNode ElementAt(int ordinal)
    {
        if ((uint)ordinal >= (uint)ElementCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        if (ordinal >= cachedFirst && ordinal < cachedFirst + cachedCount)
            return cached[ordinal % CacheSize];

        var node = Array!.Value;
        long position = reader.FirstChildPosition(node.ValueStart);
        long at = 0;
        int record = index.Structure.FindContainerStartingAt(node.ValueStart);
        if (record >= 0)
        {
            var resume = index.Structure.FindResumePoint(record, ordinal);
            if (!resume.AtOpen)
                (position, at) = (resume.Offset, resume.Ordinal);
        }

        cachedFirst = at;
        cachedCount = 0;
        while (reader.TryReadChild((byte)JsonTokenKind.StartArray, ref position, out var element, out _))
        {
            cached[at % CacheSize] = element;
            if (cachedCount == CacheSize)
                cachedFirst++;
            else
                cachedCount++;

            if (at == ordinal)
            {
                // Read a screen's worth further while here: the next rows asked for are the
                // following ones.
                ReadAhead(position, element, at);
                return element;
            }

            position = text.End(element);
            at++;
        }

        throw new InvalidOperationException($"Element {ordinal} was counted but not found.");
    }

    private void ReadAhead(long position, TreeNode from, long ordinal)
    {
        position = text.End(from);
        for (int more = 0; more < 64 && position != long.MaxValue
             && reader.TryReadChild((byte)JsonTokenKind.StartArray, ref position, out var next, out _); more++)
        {
            ordinal++;
            cached[ordinal % CacheSize] = next;
            if (cachedCount == CacheSize)
                cachedFirst++;
            else
                cachedCount++;
            position = text.End(next);
        }
    }
}
