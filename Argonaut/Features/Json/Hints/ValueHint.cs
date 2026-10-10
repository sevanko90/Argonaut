using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;
using Avalonia.Media;

namespace Argonaut.Features.Json.Hints;

public enum ValueHintKind : byte
{
    Date,
    IsoDate,
    Colour,
    Jwt,
}

/// <summary>
/// Result of classifying one scalar token's raw bytes. Payload/SchemeHint interpretation is
/// per-Kind: for Date, Payload is the parsed integer and SchemeHint is the
/// (byte)DateDecodingScheme inferred from digit length; for IsoDate, Payload is the ticks of the
/// written time (UTC ticks for an instant), SchemeHint the (byte)<see cref="IsoDateShape"/>, and
/// OffsetMinutes the offset it was written with; for Colour, Payload is the ARGB value and
/// SchemeHint 1 when it was written in hex.
/// </summary>
public readonly record struct ValueHintCandidate(ValueHintKind Kind, long Payload, byte SchemeHint, short OffsetMinutes = 0);

/// <summary>A hint as a row shows it: a chip's text, its icon or a colour swatch in place of
/// one, and what clicking it does, if anything.</summary>
public sealed record ValueHint(string Text, TreeRunIcon Icon, JsonRowLink? Link = null, Color? Swatch = null);
