using System;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Strategy for classifying a scalar token's raw bytes and formatting a hint for it, decoupled
/// from where/how the hint is rendered. This is the extension point for future hint kinds
/// beyond dates (mirrors <see cref="Argonaut.Engine.Search.ISearchMatcher"/>).
/// </summary>
public interface IValueHintProvider
{
    /// <summary>Cheap gate: false means no hint of this kind can currently render, so callers
    /// can skip classification entirely (e.g. the date scheme is Off).</summary>
    bool IsActive { get; }

    /// <summary>Whether this provider can classify a value from its first bytes alone. A value
    /// longer than the display cap is offered only to one that can, as its prefix with its full
    /// length; every other provider sees whole values.</summary>
    bool ReadsPrefixes => false;

    /// <summary>Pure, allocation-free classification of a scalar token's raw bytes. Returns
    /// false when this provider doesn't apply to the token. <paramref name="rawValue"/> is the
    /// whole value unless <see cref="ReadsPrefixes"/>, when it may be the first bytes of one
    /// <paramref name="valueLength"/> long.</summary>
    bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate);

    /// <summary>Formats the display hint for a classified candidate under current settings
    /// (an override for this value wins over the file default), from the same raw bytes
    /// <see cref="TryClassify"/> saw - a hint that needs more than the candidate carries decodes
    /// it here, for a row on screen. Null means no hint should render.</summary>
    ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset);

    /// <summary>Raised (UI thread) when settings changed such that previously formatted hints
    /// are stale and realized rows should be re-rendered.</summary>
    event EventHandler? HintsChanged;
}
