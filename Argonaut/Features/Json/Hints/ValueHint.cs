namespace Argonaut.Features.Json.Hints;

public enum ValueHintKind : byte
{
    Date
}

/// <summary>
/// Result of classifying one scalar token's raw bytes. Payload/SchemeHint interpretation is
/// per-Kind: for Date, Payload is the parsed integer and SchemeHint is the
/// (byte)DateDecodingScheme inferred from digit length.
/// </summary>
public readonly record struct ValueHintCandidate(ValueHintKind Kind, long Payload, byte SchemeHint);
