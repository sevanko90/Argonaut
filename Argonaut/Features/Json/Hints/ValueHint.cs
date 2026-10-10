using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

public enum ValueHintKind : byte
{
    Date,
    IsoDate,
}

/// <summary>
/// Result of classifying one scalar token's raw bytes. Payload/SchemeHint interpretation is
/// per-Kind: for Date, Payload is the parsed integer and SchemeHint is the
/// (byte)DateDecodingScheme inferred from digit length; for IsoDate, Payload is the ticks of the
/// written time (UTC ticks for an instant), SchemeHint the (byte)<see cref="IsoDateShape"/>, and
/// OffsetMinutes the offset it was written with.
/// </summary>
public readonly record struct ValueHintCandidate(ValueHintKind Kind, long Payload, byte SchemeHint, short OffsetMinutes = 0);

/// <summary>A hint as a row shows it: a chip's text, its icon, and what clicking it does, if
/// anything.</summary>
public sealed record ValueHint(string Text, TreeRunIcon Icon, JsonRowLink? Link = null);
