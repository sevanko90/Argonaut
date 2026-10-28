using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Xml;

/// <summary>The XML tree's remembered settings. Settable properties, not <c>init</c>: a settings
/// file that leaves one out must get its default, not null or zero.</summary>
public sealed class XmlViewSettings : ISettingsBlock<XmlViewSettings>, ITreeViewSettings
{
    public const int DefaultExpandDepth = 2;
    public const int MinExpandDepth = 0;
    public const int MaxExpandDepth = 5;

    public static string Key => "xml";

    public static JsonTypeInfo<XmlViewSettings> JsonTypeInfo => XmlSettingsJson.Default.XmlViewSettings;

    /// <summary>How many levels a newly opened tree starts expanded to, clamped to the combo's range.</summary>
    public int ExpandDepth
    {
        get;
        set => field = Math.Clamp(value, MinExpandDepth, MaxExpandDepth);
    } = DefaultExpandDepth;

    /// <summary>Whether the tree draws a faint line down each open container's children.</summary>
    public bool ShowIndentGuides { get; set; }
}

[JsonSerializable(typeof(XmlViewSettings))]
internal sealed partial class XmlSettingsJson : JsonSerializerContext;
