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
/// </summary>
public sealed record Recipe
{
    /// <summary>Stands for the line in <see cref="Template"/>.</summary>
    public const string LinePlaceholder = "{line}";

    /// <summary>One sentence for the picker; optional.</summary>
    public string? Description { get; init; }

    /// <summary>The text each line becomes, with <see cref="LinePlaceholder"/> where the line
    /// goes. Reversing needs exactly one placeholder, to know where an item starts and ends.</summary>
    public string Template { get; init; } = LinePlaceholder;

    public TextEscape Escape { get; init; } = TextEscape.None;

    /// <summary>Written between items, never after the last - which is the part a plain search
    /// and replace gets wrong.</summary>
    public string Separator { get; init; } = "";

    /// <summary>Written once before the first item, e.g. <c>(</c>.</summary>
    public string Open { get; init; } = "";

    /// <summary>Written once after the last item, e.g. <c>)</c>.</summary>
    public string Close { get; init; } = "";

    /// <summary>Trim spaces and tabs from each line before it goes into the template.</summary>
    public bool TrimLines { get; init; } = true;

    public bool SkipBlankLines { get; init; } = true;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Recipe))]
internal sealed partial class RecipeJson : JsonSerializerContext;
