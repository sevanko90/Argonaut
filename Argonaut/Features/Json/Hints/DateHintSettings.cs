using System;
using System.Collections.Generic;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Per-document session state for date hints: the file-level default decoding scheme (either
/// inferred or user-picked) plus per-value overrides, keyed by the value's byte offset. Not persisted - lives and dies with the
/// owning JsonViewModel/NdJsonViewModel. UI-thread only; background code must marshal through
/// Dispatcher.UIThread before touching this.
/// </summary>
public sealed class DateHintSettings : ObservableObject
{
    private readonly Dictionary<long, DateDecodingScheme> valueOverrides = new();
    private DateDecodingScheme fileDefaultScheme = DateDecodingScheme.Off;
    private bool isUserSelected;
    private DateHintTimeZoneMode timeZoneMode = DateHintTimeZoneMode.Local;

    public DateDecodingScheme FileDefaultScheme
    {
        get => fileDefaultScheme;
        private set => SetField(ref fileDefaultScheme, value);
    }

    /// <summary>True once the user has explicitly picked a scheme in the header dropdown -
    /// from that point, inference must never overwrite the default.</summary>
    public bool IsUserSelected
    {
        get => isUserSelected;
        private set => SetField(ref isUserSelected, value);
    }

    /// <summary>Whether decoded dates render in the machine's local time or in UTC. A plain
    /// display preference - never inferred, always user-driven, defaults to Local.</summary>
    public DateHintTimeZoneMode TimeZoneMode
    {
        get => timeZoneMode;
        private set => SetField(ref timeZoneMode, value);
    }

    /// <summary>Raised whenever a change could affect a previously rendered hint: a default
    /// scheme change or a per-value override change. Never raised for no-op changes.</summary>
    public event EventHandler? HintsChanged;

    public void SetUserDefault(DateDecodingScheme scheme)
    {
        IsUserSelected = true;
        bool changed = FileDefaultScheme != scheme;
        FileDefaultScheme = scheme;
        if (changed)
            HintsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the inferred default from the background scan. No-op if the user already
    /// picked a scheme, or if a scheme has already been inferred (FileDefaultScheme is
    /// non-Off). Returns true if the scheme was applied.</summary>
    public bool TrySetInferredDefault(DateDecodingScheme scheme)
    {
        if (IsUserSelected || FileDefaultScheme != DateDecodingScheme.Off || scheme == DateDecodingScheme.Off)
            return false;

        FileDefaultScheme = scheme;
        HintsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Sets or clears (scheme: null) the override for the value starting at
    /// <paramref name="valueOffset"/>, for the current session.</summary>
    public void SetValueOverride(long valueOffset, DateDecodingScheme? scheme)
    {
        bool changed;
        if (scheme is { } s)
        {
            changed = !valueOverrides.TryGetValue(valueOffset, out var existing) || existing != s;
            valueOverrides[valueOffset] = s;
        }
        else
        {
            changed = valueOverrides.Remove(valueOffset);
        }

        if (changed)
            HintsChanged?.Invoke(this, EventArgs.Empty);
    }

    public DateDecodingScheme GetEffectiveScheme(long valueOffset)
        => valueOverrides.TryGetValue(valueOffset, out var scheme) ? scheme : FileDefaultScheme;

    public void SetTimeZoneMode(DateHintTimeZoneMode mode)
    {
        bool changed = TimeZoneMode != mode;
        TimeZoneMode = mode;
        if (changed)
            HintsChanged?.Invoke(this, EventArgs.Empty);
    }
}
