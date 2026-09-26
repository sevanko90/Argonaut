using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Argonaut.Features.Json.Indexing;

/// <summary>
/// Folds the tokens of a <c>Utf8JsonReader</c> pass into content hashes, by
/// <see cref="JsonContentHasher"/>'s rules: a stack of open containers, each accumulating its
/// children, finalised when it closes and folded into its parent with its own name.
///
/// Whichever pass drives it, it keeps only two things: the hash of the last top-level value, and -
/// when given somewhere to put them - the hashes of containers at least a size threshold long,
/// keyed by their opening byte. Everything smaller is cheap to hash again from its bytes, which is
/// what keeps the recorded set to the size of the sparse index rather than of the document.
/// </summary>
internal sealed class JsonContentHashRecorder
{
    private struct Frame
    {
        public ulong State;
        public long Start;
        public ulong OwnNameHash;
        public bool IsObject;
        public bool HasOwnName;
    }

    private readonly Dictionary<long, ulong>? recorded;
    private readonly long recordBytes;

    private Frame[] frames = new Frame[64];
    private int frameCount;
    private ulong pendingNameHash;
    private bool hasPendingName;

    /// <param name="recorded">Where containers of at least <paramref name="recordBytes"/> go,
    /// or null to record none.</param>
    public JsonContentHashRecorder(Dictionary<long, ulong>? recorded, long recordBytes)
    {
        this.recorded = recorded;
        this.recordBytes = recordBytes;
    }

    /// <summary>The hash of the last complete top-level value.</summary>
    public ulong TopLevelHash { get; private set; }

    /// <summary>Forgets any partial state, to hash another value from the start.</summary>
    public void Reset()
    {
        frameCount = 0;
        hasPendingName = false;
        TopLevelHash = 0;
    }

    /// <summary>Takes the token <paramref name="reader"/> stands on. <paramref name="windowOffset"/>
    /// is where the reader's buffer starts in the document, so recorded keys are document offsets.
    /// The reader must read a single span, never a sequence.</summary>
    public void Observe(ref Utf8JsonReader reader, long windowOffset)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.PropertyName:
                pendingNameHash = JsonContentHasher.HashStringToken(ref reader, reader.ValueSpan);
                hasPendingName = true;
                return;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                if (frameCount == frames.Length)
                    Array.Resize(ref frames, frames.Length * 2);
                frames[frameCount++] = new Frame
                {
                    Start = windowOffset + reader.TokenStartIndex,
                    IsObject = reader.TokenType == JsonTokenType.StartObject,
                    OwnNameHash = pendingNameHash,
                    HasOwnName = hasPendingName,
                };
                hasPendingName = false;
                return;

            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
            {
                var frame = frames[--frameCount];
                ulong hash = frame.IsObject ? JsonContentHasher.FinalizeObject(frame.State) : JsonContentHasher.FinalizeArray(frame.State);
                long end = windowOffset + reader.TokenStartIndex + 1;
                if (recorded is not null && end - frame.Start >= recordBytes)
                    recorded[frame.Start] = hash;
                Fold(hash, frame.OwnNameHash, frame.HasOwnName);
                return;
            }

            case JsonTokenType.String:
                Fold(JsonContentHasher.HashStringToken(ref reader, reader.ValueSpan), pendingNameHash, hasPendingName);
                break;

            case JsonTokenType.Number:
                Fold(JsonContentHasher.HashNumber(reader.ValueSpan), pendingNameHash, hasPendingName);
                break;

            case JsonTokenType.True:
                Fold(JsonContentHasher.TrueHash, pendingNameHash, hasPendingName);
                break;

            case JsonTokenType.False:
                Fold(JsonContentHasher.FalseHash, pendingNameHash, hasPendingName);
                break;

            case JsonTokenType.Null:
                Fold(JsonContentHasher.NullHash, pendingNameHash, hasPendingName);
                break;

            default:
                return;
        }

        hasPendingName = false;
    }

    /// <summary>A finished value into the innermost open container - by name into an object,
    /// in order into an array - or, at the top, as the top-level hash.</summary>
    private void Fold(ulong valueHash, ulong nameHash, bool hasName)
    {
        if (frameCount == 0)
        {
            TopLevelHash = valueHash;
            return;
        }

        ref var parent = ref frames[frameCount - 1];
        if (parent.IsObject && hasName)
            parent.State += JsonContentHasher.MixPair(nameHash, valueHash);
        else
            parent.State = JsonContentHasher.MixOrdered(parent.State, valueHash);
    }
}
