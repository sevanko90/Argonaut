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
    // (array element or root value). One value out of the 16-bit range is reserved for
    // this so real name lengths only ever use 0 .. 0xFFFE.
    private const int NoNameLength = ushort.MaxValue;

    // Sentinel NameDelta meaning "the real back-offset didn't fit in 16 bits, look it up in
    // nameOffsetOverflow instead". A property name sits immediately before its value in
    // valid JSON, so this should be exceedingly rare in practice (only pathological
    // whitespace between name and value could push the gap past ~64KB).
    private const ushort NameDeltaOverflow = ushort.MaxValue;

    private const int KindBits = 4;
    private const int DepthBits = 12;
    private const int OffsetBits = 48;

    private const int DepthShift = KindBits;
    private const int OffsetShift = DepthShift + DepthBits;

    private const ulong KindMask = (1UL << KindBits) - 1;
    private const ulong DepthMask = (1UL << DepthBits) - 1;
    private const ulong OffsetMask = (1UL << OffsetBits) - 1;

    private const int MaxDepth = (int)DepthMask;
    private const long MaxOffset = (long)OffsetMask;
    private const int MaxNameLength = NoNameLength - 1;

    /// <summary>
    /// Compact in-memory representation of one <see cref="JsonTokenInfo"/>. Kind/Depth/Offset
    /// are bit-packed into a single 64-bit word instead of three separate fields, and
    /// NameDelta/NameLength use narrower types than the decoded (NameOffset/NameLength) longs,
    /// so this struct is 24 bytes instead of the 40-48 bytes a naive field-per-property layout
    /// would take:
    ///
    ///   Packed word (64 bits, MSB..LSB): [Offset:48][Depth:12][Kind:4]
    ///                                    Offset is the absolute byte offset; 48 bits caps
    ///                                    indexable files at ~256 TiB, which costs nothing
    ///                                    extra since Kind+Depth only need 16 of the word's
    ///                                    64 bits regardless.
    ///   Length       : int    (4 bytes) - unpacked, no cap beyond int.MaxValue
    ///   ParentIndex  : int    (4 bytes) - unpacked, no width/frequency assumption to lean on
    ///   EndIndex     : int    (4 bytes) - mutated in place once the matching End token is seen.
    ///                                     Because that mutation happens AFTER the token is
    ///                                     published to lock-free readers, this field must be
    ///                                     accessed with Volatile.Read/Volatile.Write on both
    ///                                     sides (see SegmentedAppendLog remarks); every other
    ///                                     field is immutable once published and safe to read
    ///                                     plainly.
    ///   NameDelta    : ushort (2 bytes) - Offset - NameOffset (property names sit right before
    ///                                     their value), or NameDeltaOverflow if that distance
    ///                                     didn't fit - the real NameOffset then lives in
    ///                                     nameOffsetOverflow keyed by token index
    ///   NameLength   : ushort (2 bytes) - or NoNameLength if this token has no property name
    ///
    /// Public only because it parameterizes this class's AppendLogIndexBase (a public class
    /// requires a public base-class type argument); it is an implementation detail and not
    /// intended for use outside JsonStructureIndex.
    /// </summary>
    public struct PackedToken
    {
        public ulong Packed;
        public int Length;
        public int ParentIndex;
        public int EndIndex;
        public ushort NameDelta;
        public ushort NameLength;
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

    // Content hashes (see JsonIndexOptions.ComputeContentHashes / JsonContentHasher):
    // allocated only when the option is set - 8 bytes/token when on, zero when off - and
    // indexed identically to the token log (hashes[i] is token i's hash). A container's
    // Start-token slot holds the sentinel 0 until the container closes, then receives its
    // final Merkle hash via Volatile.Write - the same publish-then-mutate pattern as
    // PackedToken.EndIndex, with the same Volatile.Read requirement on the consuming side
    // (see GetContentHash). End tokens' slots stay 0; they are not values.
    private SegmentedAppendLog<long>? hashes;

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

    // The no-options overload keeps the exact (MMapFile, IProgressReporter?, CancellationToken)
    // shape IndexedFileSession.Start's factory delegate expects, so existing call sites keep
    // passing the bare method group - optional parameters don't participate in method-group
    // conversion, which is why this is an overload and not a defaulted parameter.
    public static JsonStructureIndex StartIndexing(MMapFile file, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        => StartIndexing(file, default, progressReporter, cancellationToken);

    public static JsonStructureIndex StartIndexing(MMapFile file, JsonIndexOptions options, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var index = new JsonStructureIndex();
        if (options.ComputeContentHashes)
            index.hashes = new SegmentedAppendLog<long>();
        index.IndexingTask = Task.Run(() => index.RunIndexing(() => index.Build(file, progressReporter, cancellationToken)), cancellationToken);
        return index;
    }

    /// <summary>True while this index retains content hashes. A diff session releases its
    /// private indexes' hashes after construction because rendering only needs token metadata.</summary>
    public bool HasContentHashes => Volatile.Read(ref this.hashes) is not null;

    /// <summary>
    /// The content hash of the token at <paramref name="tokenIndex"/> (see
    /// <see cref="JsonContentHasher"/> for what it covers). Only meaningful once indexing
    /// is complete: a container's Start-token slot holds the sentinel 0 until the container
    /// closes (the write is volatile-published on close, hence the Volatile.Read here - the
    /// same pairing as <see cref="PackedToken.EndIndex"/>). End tokens always read 0.
    /// </summary>
    public long GetContentHash(int tokenIndex)
    {
        if (Volatile.Read(ref this.hashes) is not { } log)
            throw new InvalidOperationException("Content hashes are unavailable: they were not computed or have been released.");

        return Volatile.Read(ref log.ItemRef(tokenIndex));
    }

    /// <summary>Drops the optional 8-byte-per-token hash log after its sole consumer has
    /// stopped. Internal because ordinary index owners cannot know that no reader remains.</summary>
    internal void ReleaseContentHashes() => Interlocked.Exchange(ref this.hashes, null);

    /// <summary>
    /// Enriches a <see cref="JsonException"/> with best-effort line/column/byte-offset info.
    /// <see cref="JsonException.LineNumber"/>/<see cref="JsonException.BytePositionInLine"/>
    /// are relative to the current <see cref="Utf8JsonReader"/> window, which is the whole
    /// file for the sub-2GiB common case - for larger files that resume across window
    /// boundaries they may be relative to the window instead, hence "best-effort".
    /// </summary>
    protected override IndexFailure DescribeFailure(Exception ex)
    {
        if (ex is not JsonException jsonEx)
            return base.DescribeFailure(ex);

        long? byteOffset = this.ItemCount > 0 ? GetToken(this.ItemCount - 1).Offset + GetToken(this.ItemCount - 1).Length : 0;
        return new IndexFailure(
            jsonEx.Message,
            byteOffset,
            jsonEx.LineNumber.HasValue ? jsonEx.LineNumber.Value + 1 : null,
            jsonEx.BytePositionInLine.HasValue ? jsonEx.BytePositionInLine.Value + 1 : null,
            this.ItemCount);
    }

    /// <summary>One open container's hash accumulation state, kept in lockstep with the
    /// <c>openContainers</c> stack in <see cref="Build"/>. See <see cref="JsonContentHasher"/>
    /// for the combining rules; OwnNameHash is the container's *own* property-name hash,
    /// held here so it can be folded into the parent on close (a child's own hash never
    /// contains its key).</summary>
    private struct HashFrame
    {
        public ulong State;
        public bool IsObject;
        public ulong OwnNameHash;
        public bool HasOwnName;
    }

    /// <summary>Folds a finished child value (scalar or closed container) into the current
    /// innermost open container's accumulator, per <see cref="JsonContentHasher"/>'s rules.
    /// No-op at the document root.</summary>
    private static void FoldIntoParent(HashFrame[] frames, int frameCount, ulong valueHash, ulong nameHash, bool hasName)
    {
        if (frameCount == 0)
            return;

        ref var parent = ref frames[frameCount - 1];
        if (parent.IsObject && hasName)
            parent.State += JsonContentHasher.MixPair(nameHash, valueHash);
        else
            parent.State = JsonContentHasher.MixOrdered(parent.State, valueHash);
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

        // Hash state - untouched (single null-check per token) when hashing is off.
        var hashLog = this.hashes;
        var hashFrames = hashLog is null ? Array.Empty<HashFrame>() : new HashFrame[64];
        int hashFrameCount = 0;
        ulong pendingNameHash = 0;

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
                    if (hashLog is not null)
                        pendingNameHash = JsonContentHasher.HashStringToken(ref reader, file.GetSpan(rawTokenOffset, rawTokenLength));
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

                if (hashLog is not null)
                {
                    // Exactly one hashLog.Add per token keeps the two logs index-aligned.
                    // The scalar/close adds land before the token itself is published, so a
                    // reader that can see token i can always see hashes[i].
                    switch (tokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            hashLog.Add(0); // sentinel until the container closes
                            if (hashFrameCount == hashFrames.Length)
                                Array.Resize(ref hashFrames, hashFrames.Length * 2);
                            hashFrames[hashFrameCount++] = new HashFrame
                            {
                                State = 0,
                                IsObject = tokenType == JsonTokenType.StartObject,
                                OwnNameHash = pendingNameHash,
                                HasOwnName = pendingNameLength >= 0
                            };
                            break;

                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                        {
                            var frame = hashFrames[--hashFrameCount];
                            ulong finalHash = frame.IsObject
                                ? JsonContentHasher.FinalizeObject(frame.State)
                                : JsonContentHasher.FinalizeArray(frame.State);
                            // Publish-then-mutate, same as EndIndex below: the Start token's
                            // slot was published as 0 and is finalized here, so the write must
                            // be volatile (paired with the Volatile.Read in GetContentHash).
                            Volatile.Write(ref hashLog.ItemRef(startIndex), (long)finalHash);
                            hashLog.Add(0); // the End token itself is not a value
                            FoldIntoParent(hashFrames, hashFrameCount, finalHash, frame.OwnNameHash, frame.HasOwnName);
                            break;
                        }

                        default:
                        {
                            ulong scalarHash = tokenType switch
                            {
                                JsonTokenType.String => JsonContentHasher.HashStringToken(ref reader, file.GetSpan(rawTokenOffset, rawTokenLength)),
                                JsonTokenType.Number => JsonContentHasher.HashNumber(file.GetSpan(rawTokenOffset, rawTokenLength)),
                                JsonTokenType.True => JsonContentHasher.TrueHash,
                                JsonTokenType.False => JsonContentHasher.FalseHash,
                                _ => JsonContentHasher.NullHash
                            };
                            hashLog.Add((long)scalarHash);
                            FoldIntoParent(hashFrames, hashFrameCount, scalarHash, pendingNameHash, pendingNameLength >= 0);
                            break;
                        }
                    }
                }

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
        if (rawTokenOffset < 0 || rawTokenOffset > MaxOffset)
            throw new NotSupportedException($"File offset exceeds the supported limit of {MaxOffset} bytes (~256 TiB).");
        if (depth > MaxDepth)
            throw new NotSupportedException($"JSON nesting depth exceeds the supported limit of {MaxDepth}.");

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
        packed |= ((ulong)rawTokenOffset & OffsetMask) << OffsetShift;

        return new PackedToken
        {
            Packed = packed,
            Length = rawTokenLength,
            ParentIndex = parentIndex,
            EndIndex = -1,
            NameDelta = nameDelta,
            NameLength = (ushort)nameLengthField
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
        long offset = (long)((packed.Packed >> OffsetShift) & OffsetMask);
        int length = packed.Length;
        int nameLengthField = packed.NameLength;

        // EndIndex is the one field the writer mutates after publication (when the matching
        // End token is found), so it must be read volatile - paired with the Volatile.Write
        // in Build. A plain read (or a whole-struct copy) could observe a stale or torn
        // value; a lock here instead would put 10-20ns on every token read, which callers
        // like DescribeChildCount multiply by tens of thousands per rendered row.
        int endIndex = Volatile.Read(ref packed.EndIndex);

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
