using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Ui.Documents.Numbering;

/// <summary>How excerpts number their lines, remembered across runs.</summary>
public sealed class LineNumberingSettings : ISettingsBlock<LineNumberingSettings>
{
    public static string Key => "lineNumbering";

    public static JsonTypeInfo<LineNumberingSettings> JsonTypeInfo => LineNumberingSettingsJson.Default.LineNumberingSettings;

    /// <summary>True numbers an excerpt's lines as the file does; false counts from 1 at the
    /// excerpt's first line.</summary>
    public bool NumberExcerptsFromSource { get; set; } = true;
}

[JsonSerializable(typeof(LineNumberingSettings))]
internal sealed partial class LineNumberingSettingsJson : JsonSerializerContext;
