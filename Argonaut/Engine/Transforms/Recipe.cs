using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argonaut.Engine.Transforms;

/// <summary>Which way a transform runs. A recipe is saved once and offered both ways, so
/// "quote for SQL" and "unquote a SQL list" are one recipe rather than two to keep in step; a
/// codec encodes forward and decodes in reverse.</summary>
public enum TransformDirection
{
    Forward,
    Reverse,
}

/// <summary>How a line is escaped as it goes into a template, so the result stays valid
/// inside the quotes the template puts round it.</summary>
public enum TextEscape
{
    None,

    /// <summary><c>'</c> doubled, for a single-quoted SQL string.</summary>
    Sql,

    /// <summary><c>"</c> doubled, for a double-quoted CSV field.</summary>
    Csv,

    /// <summary>A JSON string's escapes: quote, backslash and control characters.</summary>
    Json,
}

/// <summary>
/// A line template the user can edit, save and apply to a document: each line is placed into
/// <see cref="Template"/> and the results joined - quoting a column of values for a SQL
/// <c>IN</c>, or joining lines with a comma. Reversed, it takes such a list apart again, one item
/// per line. Saved as one JSON file (see <see cref="IRecipeStore"/>).
///
/// Fixed encodings are not recipes - nothing about them can be changed - and live in
/// <see cref="CodecRunner"/> instead.
///
/// The properties are settable rather than <c>init</c> for the JSON reader's sake: the source
/// generator assigns every init-only property, missing ones as null or false, which turned a
/// preset that left out <c>open</c> into a null and quietly cleared both trimming options.
/// Settable, it sets only what the file has, and a setting left out keeps its default here.
/// </summary>
public sealed record Recipe
{
    /// <summary>Stands for the line in <see cref="Template"/>.</summary>
    public const string LinePlaceholder = "{line}";

    /// <summary>One sentence for the picker; optional.</summary>
    public string? Description { get; set; }

    /// <summary>The text each line becomes, with <see cref="LinePlaceholder"/> where the line
    /// goes. Reversing needs exactly one placeholder, to know where an item starts and ends.</summary>
    public string Template { get; set; } = LinePlaceholder;

    public TextEscape Escape { get; set; } = TextEscape.None;

    /// <summary>Written between items, never after the last - which is the part a plain search
    /// and replace gets wrong.</summary>
    public string Separator { get; set; } = "";

    /// <summary>Written once before the first item, e.g. <c>(</c>.</summary>
    public string Open { get; set; } = "";

    /// <summary>Written once after the last item, e.g. <c>)</c>.</summary>
    public string Close { get; set; } = "";

    /// <summary>Trim spaces and tabs from each line before it goes into the template.</summary>
    public bool TrimLines { get; set; } = true;

    public bool SkipBlankLines { get; set; } = true;
}

/// <summary>Writes <see cref="TextEscape"/> as the presets spell it - <c>"sql"</c>, not
/// <c>"Sql"</c> - so a recipe the app saved reads like one written by hand. Reading ignores case
/// either way.</summary>
internal sealed class CamelCaseEscapeConverter() : JsonStringEnumConverter<TextEscape>(JsonNamingPolicy.CamelCase);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(CamelCaseEscapeConverter)],
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Recipe))]
internal sealed partial class RecipeJson : JsonSerializerContext;
