using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Features.Json;

/// <summary>The JSON tree's remembered settings, shared by the NDJSON view's selected-line tree.</summary>
public sealed class JsonViewSettings : ISettingsBlock<JsonViewSettings>
{
    public const int DefaultExpandDepth = 2;
    public const int MinExpandDepth = 0;
    public const int MaxExpandDepth = 5;

    public static string Key => "json";

    public static JsonTypeInfo<JsonViewSettings> JsonTypeInfo => JsonSettingsJson.Default.JsonViewSettings;

    /// <summary>How many levels a newly opened tree starts expanded to, clamped to the combo's range.</summary>
    public int ExpandDepth
    {
        get;
        set => field = Math.Clamp(value, MinExpandDepth, MaxExpandDepth);
    } = DefaultExpandDepth;
}

[JsonSerializable(typeof(JsonViewSettings))]
internal sealed partial class JsonSettingsJson : JsonSerializerContext;
