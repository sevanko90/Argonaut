using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Features.Raw;

/// <summary>The raw viewer's remembered settings.</summary>
public sealed class RawViewSettings : ISettingsBlock<RawViewSettings>
{
    /// <summary>The selectable wrap widths, ascending. Combo indices map into this array.</summary>
    public static readonly int[] Widths = [80, 160, 512];

    public const int DefaultWrapWidth = 160;

    public static string Key => "raw";

    public static JsonTypeInfo<RawViewSettings> JsonTypeInfo => RawSettingsJson.Default.RawViewSettings;

    /// <summary>Always one of <see cref="Widths"/>: anything else snaps to the nearest, so a
    /// hand-edited or stale settings file can never produce a width the combo cannot represent.</summary>
    public int WrapWidth
    {
        get;
        set
        {
            int best = Widths[0];
            foreach (int candidate in Widths)
            {
                if (Math.Abs(candidate - value) < Math.Abs(best - value))
                    best = candidate;
            }

            field = best;
        }
    } = DefaultWrapWidth;
}

[JsonSerializable(typeof(RawViewSettings))]
internal sealed partial class RawSettingsJson : JsonSerializerContext;
