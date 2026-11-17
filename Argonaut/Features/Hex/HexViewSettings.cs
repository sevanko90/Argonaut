using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Features.Hex;

/// <summary>The hex view's remembered settings. Settable properties, not <c>init</c>: a settings
/// file that leaves one out must get its default, not zero.</summary>
public sealed class HexViewSettings : ISettingsBlock<HexViewSettings>
{
    public static string Key => "hex";

    public static JsonTypeInfo<HexViewSettings> JsonTypeInfo => HexSettingsJson.Default.HexViewSettings;

    /// <summary>How many bytes the hex panel draws together; anything but 1, 2, 4 or 8 reads as 1.</summary>
    public int GroupSize
    {
        get;
        set => field = HexRows.IsGroupSize(value) ? value : 1;
    } = 1;
}

[JsonSerializable(typeof(HexViewSettings))]
internal sealed partial class HexSettingsJson : JsonSerializerContext;
