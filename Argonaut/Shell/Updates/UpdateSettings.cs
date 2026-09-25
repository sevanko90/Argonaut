using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Updates;

/// <summary>
/// Whether the silent startup update check runs (the About dialog's checkbox), and when it last
/// did, which throttles it.
/// </summary>
public sealed class UpdateSettings : ISettingsBlock<UpdateSettings>
{
    public static string Key => "updates";

    public static JsonTypeInfo<UpdateSettings> JsonTypeInfo => UpdateSettingsJson.Default.UpdateSettings;

    public bool CheckOnStartup { get; set; } = true;

    public DateTimeOffset? LastStartupCheckUtc { get; set; }
}

[JsonSerializable(typeof(UpdateSettings))]
internal sealed partial class UpdateSettingsJson : JsonSerializerContext;
