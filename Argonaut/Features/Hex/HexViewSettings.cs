using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;
using Argonaut.Features.Hex.Values;

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

    /// <summary>Whether the side panel - the Values tab - is shown beside the dump.</summary>
    public bool ShowsSidePanel { get; set; } = true;

    /// <summary>The side panel's tab last shown.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HexPanelTab>))]
    public HexPanelTab SidePanelTab { get; set; } = HexPanelTab.Values;

    /// <summary>Whether the character column reads its bytes as UTF-8 rather than ASCII.</summary>
    public bool ReadsUtf8 { get; set; }

    /// <summary>The byte order the Values tab reads in: this machine's until one is chosen.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ByteOrder>))]
    public ByteOrder ValuesByteOrder { get; set; } = HexValuesViewModel.SystemOrder;
}

/// <summary>The side panel's tabs.</summary>
public enum HexPanelTab
{
    Values,
    Structures,
    Struct,
}

[JsonSerializable(typeof(HexViewSettings))]
internal sealed partial class HexSettingsJson : JsonSerializerContext;
