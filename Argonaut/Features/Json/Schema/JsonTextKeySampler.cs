using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// The outermost object's property names read straight off a window of JSON text - the start of
/// an NDJSON line - for matching schemas where no tree exists to walk
/// (<see cref="JsonDocumentKeySampler"/> is the tree-walking counterpart).
///
/// The window may stop anywhere, so it is read as a block that is not final: a value cut off by
/// the window's end ends the sample, keeping every name before it. Malformed text ends it the
/// same way. Names are copied out, since the window is a view of the document.
/// </summary>
public static class JsonTextKeySampler
{
    /// <summary>How much of a line is read - enough for the names of any object a schema
    /// describes, unless an early member's value is longer than this, which ends the sample.</summary>
    public const int WindowBytes = 64 * 1024;

    /// <summary>Up to <see cref="JsonDocumentKeySampler.MaxKeys"/> names of the outermost
    /// object, or empty for anything but an object.</summary>
    public static IReadOnlyList<byte[]> ReadTopLevelKeys(ReadOnlySpan<byte> window)
    {
        var keys = new List<byte[]>();
        var reader = new Utf8JsonReader(window, isFinalBlock: false, state: default);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Array.Empty<byte[]>();

            while (keys.Count < JsonDocumentKeySampler.MaxKeys && reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                keys.Add(reader.ValueSpan.ToArray());

                // Onto the value, then past it: a container is skipped whole, and one the window
                // cuts off cannot be, which ends the sample.
                if (!reader.Read() || !reader.TrySkip())
                    break;
            }
        }
        catch (JsonException)
        {
            // Malformed past this point: the names before it stand.
        }

        return keys;
    }
}
