using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

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
/// A structural (non-decoding) record of one JSON value/container token, decoded on demand
/// from the compact <see cref="JsonStructureIndex.PackedToken"/> representation actually held
/// in memory (see <see cref="JsonStructureIndex.GetToken"/>). Text is never materialized here -
/// callers re-read (Offset, Length) from the backing MMapFile on demand, the same way
/// FileOffsetIndex/FileLineSpan works for NDJSON.
/// </summary>
/// <param name="Kind">StartObject/EndObject/StartArray/EndArray, or the scalar kind (String/Number/True/False/Null).</param>
/// <param name="Depth">Container nesting depth of this token (0 at the document root).</param>
/// <param name="Offset">Absolute byte offset in the file of this token's content (quotes/brackets excluded for strings).</param>
/// <param name="Length">Byte length of this token's content at <paramref name="Offset"/>.</param>
/// <param name="ParentIndex">Token index of the enclosing container's Start token, or -1 at the document root.</param>
/// <param name="EndIndex">For a Start token, the token index of its matching End token; -1 until the container closes. Unused for scalars.</param>
/// <param name="NameOffset">Absolute byte offset of this token's property name, or -1 if it has none (array element/root value).</param>
/// <param name="NameLength">Byte length of the property name at <paramref name="NameOffset"/>, or -1 if there is no name.</param>
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
///
/// The per-token records are additionally bit-packed into <see cref="PackedToken"/> (see
/// that type for the layout) to keep steady-state memory down on multi-million-token
/// documents; <see cref="GetToken"/> unpacks back to the friendly <see cref="JsonTokenInfo"/>
/// shape so callers never see the packed representation.
/// </summary>
public sealed class JsonStructureIndex : AppendLogIndexBase<JsonStructureIndex.PackedToken>, IFileIndexer
{
    // Sentinel NameLength stored in the packed word when a token has no property name
    // (array element or root value). One value out of the 24-bit range is reserved for
    // this so real name lengths only ever use 0 .. 0xFFFFFE.
    private const int NoNameLength = 0xFFFFFF;

    // Sentinel NameDelta meaning "the real back-offset didn't fit in 16 bits, look it up in
    // nameOffsetOverflow instead". A property name sits immediately before its value in
    // valid JSON, so this should be exceedingly rare in practice (only pathological
    // whitespace between name and value could push the gap past ~64KB).
    private const ushort NameDeltaOverflow = ushort.MaxValue;

    private const int KindBits = 4;
    private const int DepthBits = 12;
    private const int LengthBits = 24;
    private const int NameLengthBits = 24;

    private const int DepthShift = KindBits;
    private const int LengthShift = DepthShift + DepthBits;
    private const int NameLengthShift = LengthShift + LengthBits;

    private const ulong KindMask = (1UL << KindBits) - 1;
    private const ulong DepthMask = (1UL << DepthBits) - 1;
    private const ulong LengthMask = (1UL << LengthBits) - 1;
    private const ulong NameLengthMask = (1UL << NameLengthBits) - 1;

    private const int MaxDepth = (int)DepthMask;
    private const int MaxLength = (int)LengthMask;
    private const int MaxNameLength = NoNameLength - 1;

    /// <summary>
    /// Compact in-memory representation of one <see cref="JsonTokenInfo"/>. Kind/Depth/Length/
    /// NameLength are bit-packed into a single 64-bit word instead of four separate ints, and
    /// Offset/NameDelta use narrower types than the decoded (Offset/NameOffset) longs, so this
    /// struct is 24 bytes instead of the 40-48 bytes a naive field-per-property layout would take:
    ///
    ///   Packed word (64 bits, MSB..LSB): [NameLength:24][Length:24][Depth:12][Kind:4]
    ///   Offset       : uint   (4 bytes) - absolute byte offset, caps indexable files at ~4 GiB
    ///   NameDelta    : ushort (2 bytes) - Offset - NameOffset (property names sit right before
    ///                                     their value), or NameDeltaOverflow if that distance
    ///                                     didn't fit - the real NameOffset then lives in
    ///                                     nameOffsetOverflow keyed by token index
    ///   ParentIndex  : int    (4 bytes) - unpacked, no width/frequency assumption to lean on
    ///   EndIndex     : int    (4 bytes) - mutated in place once the matching End token is seen.
    ///                                     Because that mutation happens AFTER the token is
    ///                                     published to lock-free readers, this field must be
    ///                                     accessed with Volatile.Read/Volatile.Write on both
    ///                                     sides (see SegmentedAppendLog remarks); every other
    ///                                     field is immutable once published and safe to read
    ///                                     plainly.
    ///
    /// Public only because it parameterizes this class's AppendLogIndexBase (a public class
    /// requires a public base-class type argument); it is an implementation detail and not
    /// intended for use outside JsonStructureIndex.
    /// </summary>
    public struct PackedToken
    {
        public ulong Packed;
        public uint Offset;
        public ushort NameDelta;
        public int ParentIndex;
        public int EndIndex;
    }

    // The token log itself (base.items) is single-writer/multi-reader and lock-free - see
    // AppendLogIndexBase/SegmentedAppendLog. The one field mutated after publication
    // (EndIndex) uses Volatile.Read/Volatile.Write on both sides; see the PackedToken
    // layout doc.

    // Guards ONLY the cold overflow/failure state below (the waiter machinery has its own
    // lock in the base). Nothing on the per-token hot path takes this lock - that is the
    // point of the lock-free log (an uncontended lock pair is 10-20ns, times ~3 per token,
    // times millions of tokens).
    private readonly Lock overflowSync = new();

    // Populated only in the rare case a property name's back-offset from its value doesn't
    // fit in PackedToken.NameDelta (see NameDeltaOverflow). Expected to stay empty/near-empty.
    // Dictionary<K,V> is not safe for read-during-resize, so BOTH sides access it under
    // overflowSync; that's fine because the overflow path is pathological-whitespace-only cold.
    private readonly Dictionary<int, long> nameOffsetOverflow = new();

    private Exception? failure;

    private JsonStructureIndex()
    {
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public string ItemNoun => "tokens";

    /// <summary>
    /// Number of tokens indexed so far (may grow until <see cref="AppendLogIndexBase{T}.IsComplete"/> is true).
    /// </summary>
    public int TokenCount => this.ItemCount;

    public JsonTokenInfo GetToken(int index)
    {
        return Unpack(index, ref this.items.ItemRef(index));
    }

    /// <summary>
    /// Waits (asynchronously) until at least <paramref name="targetCount"/> tokens are indexed,
    /// or indexing completes with fewer tokens than that.
    /// </summary>
    public Task WaitForTokenCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    /// <summary>
    /// Waits until the token at <paramref name="tokenIndex"/> has been indexed (i.e. TokenCount &gt; tokenIndex),
    /// or indexing completes. Used when expanding into a region of the document not yet indexed.
    /// </summary>
    public Task WaitForTokenIndexedAsync(int tokenIndex) => WaitForTokenCountAsync(tokenIndex + 1);

    // Checked every 65536 tokens inside the hot per-token loop - frequent enough that a
    // caller cancelling (e.g. window close mid-index) stops this loop within a few
    // milliseconds, rare enough that the check (one branch on a bitmask) costs nothing
    // measurable against the per-token budget.
    private const int CancellationCheckMask = 0xFFFF;

    public static JsonStructureIndex StartIndexing(MMapFile file, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var index = new JsonStructureIndex();
        index.IndexingTask = Task.Run(() => index.Run(file, progressReporter, cancellationToken), cancellationToken);
        return index;
    }

    private void Run(MMapFile file, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        try
        {
            Build(file, progressReporter, cancellationToken);
        }
        catch (Exception ex)
        {
            lock (overflowSync)
                failure = ex;
            throw;
        }
        finally
        {
            this.MarkComplete();
        }
    }

    private void Build(MMapFile file, IProgressReporter? progressReporter, CancellationToken cancellationToken)
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

        // Progress is reported from inside the token loop in ~5% steps: parsing runs over a
        // handful of giant windows (usually exactly one), so the outer loop no longer
        // iterates often enough to hang reporting off it.
        long reportStep = Math.Max(1, length / 20);
        long nextReport = reportStep;

        while (offset < length)
        {
            // Parse directly over the mapped bytes - zero copies. A span is capped at
            // int.MaxValue bytes, so a sub-2GiB file (the common case) is parsed in one
            // pass with no reader-state resumption; larger files resume across window
            // boundaries the same way the old copied chunks did.
            int size = (int)Math.Min(int.MaxValue, length - offset);
            bool isFinalBlock = offset + size >= length;
            var reader = new Utf8JsonReader(file.GetSpan(offset, size), isFinalBlock, state);

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

                int parentIndex;
                int depth;
                int startIndex = -1;

                if (tokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    // The container being closed is still on the stack here (it's popped
                    // below), so peeking/counting the stack directly would attribute this
                    // End token to its own container as parent, one depth too deep. Instead
                    // it must mirror its Start token's own parent/depth exactly, so the
                    // closing bracket lines up visually with the opening one.
                    startIndex = openContainers.Pop();
                    var startToken = GetToken(startIndex);
                    parentIndex = startToken.ParentIndex;
                    depth = startToken.Depth;
                }
                else
                {
                    parentIndex = openContainers.Count > 0 ? openContainers.Peek() : -1;
                    depth = openContainers.Count;
                }

                // Single writer, so the pre-Add Count is exactly this token's index.
                int tokenIndex = this.items.Count;
                this.items.Add(Pack(kind, depth, rawTokenOffset, rawTokenLength,
                    parentIndex, pendingNameOffset, pendingNameLength, tokenIndex));

                pendingNameOffset = -1;
                pendingNameLength = -1;

                if (tokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    openContainers.Push(tokenIndex);
                }
                else if (tokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    // EndIndex is the one field mutated after its token was published to
                    // lock-free readers, so the write must be volatile (paired with the
                    // Volatile.Read in Unpack). A plain write could be observed torn or
                    // reordered by a concurrent GetToken; the alternative - locking here
                    // and in every reader - costs 10-20ns per acquisition at millions of
                    // tokens, which is what this design exists to avoid.
                    Volatile.Write(ref this.items.ItemRef(startIndex).EndIndex, tokenIndex);
                }

                this.OnItemsPublished(tokenIndex + 1);

                if ((tokenIndex & CancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                long consumedSoFar = offset + reader.BytesConsumed;
                if (consumedSoFar >= nextReport)
                {
                    progressReporter?.Report("Indexing", consumedSoFar, length);
                    while (nextReport <= consumedSoFar)
                        nextReport += reportStep;
                }
            }

            long consumed = reader.BytesConsumed;
            state = reader.CurrentState;

            if (consumed == 0 && !isFinalBlock)
                throw new NotSupportedException("A single JSON token larger than 2 GiB is not supported.");

            offset += consumed;
        }

        progressReporter?.Report("Indexing", length, length);
    }

    /// <summary>
    /// Encodes one token into its compact <see cref="PackedToken"/> form. Writer-thread
    /// only; lock-free except the pathological name-offset overflow case (see
    /// <see cref="NameDeltaOverflow"/>), which records into <see cref="nameOffsetOverflow"/>
    /// under <see cref="sync"/>.
    /// </summary>
    private PackedToken Pack(JsonTokenKind kind, int depth, long rawTokenOffset, int rawTokenLength,
        int parentIndex, long pendingNameOffset, int pendingNameLength, int tokenIndex)
    {
        if (rawTokenOffset < 0 || rawTokenOffset > uint.MaxValue)
            throw new NotSupportedException("File offset exceeds the ~4 GiB indexable limit.");
        if (depth > MaxDepth)
            throw new NotSupportedException($"JSON nesting depth exceeds the supported limit of {MaxDepth}.");
        if (rawTokenLength > MaxLength)
            throw new NotSupportedException($"Token length exceeds the supported limit of {MaxLength} bytes.");

        int nameLengthField;
        ushort nameDelta;
        if (pendingNameLength < 0)
        {
            nameLengthField = NoNameLength;
            nameDelta = 0;
        }
        else
        {
            if (pendingNameLength > MaxNameLength)
                throw new NotSupportedException($"Property name length exceeds the supported limit of {MaxNameLength} bytes.");

            nameLengthField = pendingNameLength;
            long delta = rawTokenOffset - pendingNameOffset;
            if (delta >= NameDeltaOverflow)
            {
                nameDelta = NameDeltaOverflow;
                // The entry lands in the dictionary before the log's Add publishes this
                // token, so any reader that can see the token can also see its entry; the
                // lock is only for the dictionary's own internal consistency (readers may
                // look up older entries while this insert resizes it).
                lock (overflowSync)
                    nameOffsetOverflow[tokenIndex] = pendingNameOffset;
            }
            else
            {
                nameDelta = (ushort)delta;
            }
        }

        ulong packed = (ulong)(byte)kind & KindMask;
        packed |= ((ulong)depth & DepthMask) << DepthShift;
        packed |= ((ulong)(uint)rawTokenLength & LengthMask) << LengthShift;
        packed |= ((ulong)(uint)nameLengthField & NameLengthMask) << NameLengthShift;

        return new PackedToken
        {
            Packed = packed,
            Offset = (uint)rawTokenOffset,
            NameDelta = nameDelta,
            ParentIndex = parentIndex,
            EndIndex = -1
        };
    }

    /// <summary>
    /// Decodes a stored <see cref="PackedToken"/> back into the public <see cref="JsonTokenInfo"/>
    /// shape. Lock-free: all fields of a published token are immutable except EndIndex,
    /// which is read volatile (see below).
    /// </summary>
    private JsonTokenInfo Unpack(int tokenIndex, ref PackedToken packed)
    {
        var kind = (JsonTokenKind)(packed.Packed & KindMask);
        int depth = (int)((packed.Packed >> DepthShift) & DepthMask);
        int length = (int)((packed.Packed >> LengthShift) & LengthMask);
        int nameLengthField = (int)((packed.Packed >> NameLengthShift) & NameLengthMask);

        // EndIndex is the one field the writer mutates after publication (when the matching
        // End token is found), so it must be read volatile - paired with the Volatile.Write
        // in Build. A plain read (or a whole-struct copy) could observe a stale or torn
        // value; a lock here instead would put 10-20ns on every token read, which callers
        // like DescribeChildCount multiply by tens of thousands per rendered row.
        int endIndex = Volatile.Read(ref packed.EndIndex);

        long offset = packed.Offset;
        int nameLength;
        long nameOffset;
        if (nameLengthField == NoNameLength)
        {
            nameLength = -1;
            nameOffset = -1;
        }
        else
        {
            nameLength = nameLengthField;
            if (packed.NameDelta == NameDeltaOverflow)
            {
                lock (overflowSync)
                    nameOffset = nameOffsetOverflow[tokenIndex];
            }
            else
            {
                nameOffset = offset - packed.NameDelta;
            }
        }

        return new JsonTokenInfo(kind, depth, offset, length, packed.ParentIndex, endIndex, nameOffset, nameLength);
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
