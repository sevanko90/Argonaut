using System;

namespace Argonaut.Engine.Hints;

/// <summary>How a document's dates are read: which scheme its numbers are decoded with, file-wide
/// and per value, and which time zone dates are shown in. Read by the date-shaped providers.</summary>
public interface IDateHintSettings
{
    DateDecodingScheme FileDefaultScheme { get; }

    DateHintTimeZoneMode TimeZoneMode { get; }

    /// <summary>The scheme for the value starting at <paramref name="valueOffset"/>: its own
    /// override when it has one, else the file default.</summary>
    DateDecodingScheme GetEffectiveScheme(long valueOffset);

    /// <summary>Raised whenever a change could affect a previously rendered hint.</summary>
    event EventHandler? HintsChanged;
}
