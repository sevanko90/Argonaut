using System;
using Argonaut.Features.Json;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Strategy for classifying a scalar token's raw bytes and formatting a hint for it, decoupled
/// from where/how the hint is rendered. This is the extension point for future hint kinds
/// beyond dates (mirrors <see cref="Argonaut.Features.Search.ISearchMatcher"/>).
/// </summary>
public interface IValueHintProvider
{
    /// <summary>Cheap gate: false means no hint of this kind can currently render, so callers
    /// can skip classification entirely (e.g. the date scheme is Off).</summary>
    bool IsActive { get; }

    /// <summary>Pure, allocation-free classification of a scalar token's raw bytes. Returns
    /// false when this provider doesn't apply to the token.</summary>
    bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, out ValueHintCandidate candidate);

    /// <summary>Formats the display hint for a classified candidate under current settings
    /// (a per-token override wins over the file default). Null means no hint should render.</summary>
    string? FormatHint(in ValueHintCandidate candidate, int tokenIndex);

    /// <summary>Raised (UI thread) when settings changed such that previously formatted hints
    /// are stale and realized rows should be re-rendered.</summary>
    event EventHandler? HintsChanged;
}
