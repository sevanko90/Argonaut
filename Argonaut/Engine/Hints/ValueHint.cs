using System;

namespace Argonaut.Engine.Hints;

public enum ValueHintKind : byte
{
    Date,
    IsoDate,
    Colour,
    Jwt,
    Url,
    Cron,
    EmbeddedJson,
    Base64,
}

/// <summary>What a scalar is as its format writes it - the only thing about a value's syntax a
/// hint needs. A format with no such distinction (an unquoted config value) offers its values as
/// <see cref="String"/>.</summary>
public enum ValueShape : byte
{
    String,
    Number,
}

/// <summary>What a hint is about, for a view to draw as it draws such things.</summary>
public enum ValueHintSymbol : byte
{
    None,
    /// <summary>A moment or a schedule.</summary>
    Time,
    /// <summary>A credential - a decoded token.</summary>
    Key,
    /// <summary>A web address.</summary>
    OpenLink,
    /// <summary>A mail address.</summary>
    Mail,
    /// <summary>A JSON document held inside the value.</summary>
    Json,
    /// <summary>Encoded binary data.</summary>
    Binary,
    /// <summary>A picture.</summary>
    Image,
}

/// <summary>How insistently a hint is shown: a chip is always there; an action appears where the
/// value has the user's attention (the hovered or selected row).</summary>
public enum ValueHintEmphasis : byte
{
    Chip,
    Action,
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

/// <summary>What a value reads as, for any view to show its own way: the text, what it is about,
/// how insistently to show it, and what it offers. <paramref name="Colour"/> is an ARGB colour the
/// value names; <paramref name="Address"/> a web or mail address it is, safe to open (see
/// <see cref="UrlHintProvider.IsSafe"/>); <paramref name="Expands"/> says the value hides a
/// document <see cref="HintExpansion"/> can show in full.</summary>
public sealed record ValueHint(ValueHintKind Kind, string Text, ValueHintSymbol Symbol,
    ValueHintEmphasis Emphasis = ValueHintEmphasis.Chip, uint? Colour = null, Uri? Address = null, bool Expands = false);
