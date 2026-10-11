using System;
using System.Collections.Generic;

namespace Argonaut.Engine.Hints;

/// <summary>
/// Strategy for classifying a scalar's raw bytes and saying what it reads as, decoupled from the
/// format it came from and from how a view shows it. The extension point for new hint kinds
/// (mirrors <see cref="Argonaut.Engine.Search.ISearchMatcher"/>).
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

    /// <summary>Pure, allocation-free classification of a scalar's raw bytes - a string's
    /// content as written, escapes and all, without its quotes. Returns false when this provider
    /// doesn't apply. <paramref name="rawValue"/> is the whole value unless
    /// <see cref="ReadsPrefixes"/>, when it may be the first bytes of one
    /// <paramref name="valueLength"/> long.</summary>
    bool TryClassify(ValueShape shape, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate);

    /// <summary>Formats the hint for a classified candidate under current settings (an override
    /// for the value at <paramref name="valueOffset"/> wins over the file default), from the same
    /// raw bytes <see cref="TryClassify"/> saw - a hint that needs more than the candidate carries
    /// decodes it here, for a value on screen. Null means no hint should render.</summary>
    ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset);

    /// <summary>Raised (UI thread) when settings changed such that previously formatted hints
    /// are stale and realized rows should be re-rendered.</summary>
    event EventHandler? HintsChanged;
}

/// <summary>Running a value past a list of providers, the first to read it winning.</summary>
public static class ValueHints
{
    public static ValueHint? Read(IReadOnlyList<IValueHintProvider> providers, ValueShape shape, ReadOnlySpan<byte> rawValue,
        long valueLength, long valueOffset)
    {
        bool isWhole = valueLength == rawValue.Length;
        foreach (var provider in providers)
        {
            if (provider.IsActive && (isWhole || provider.ReadsPrefixes) && provider.TryClassify(shape, rawValue, valueLength, out var candidate)
                && provider.FormatHint(in candidate, rawValue, valueLength, valueOffset) is { } hint)
                return hint;
        }

        return null;
    }
}
