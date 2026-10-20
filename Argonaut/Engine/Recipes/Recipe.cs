using System.Text.Json.Serialization;

namespace Argonaut.Engine.Recipes;

/// <summary>What a recipe does to its input.</summary>
public enum RecipeKind
{
    /// <summary>Each line is placed into <see cref="Recipe.Template"/> and the results joined -
    /// quoting a column of values for a SQL <c>IN</c>, or joining lines with a comma. Reversed,
    /// it takes such a list apart again, one item per line.</summary>
    Template,

    /// <summary>The input is encoded with <see cref="Recipe.Codec"/>; reversed, decoded.</summary>
    Codec,
}

/// <summary>Which way a recipe runs. A recipe is saved once and offered both ways, so
/// "quote for SQL" and "unquote a SQL list" are one recipe rather than two to keep in step.</summary>
public enum RecipeDirection
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

public enum RecipeCodec
{
    /// <summary>The body of a JSON string - escaped without the surrounding quotes.</summary>
    JsonString,

    /// <summary>Percent-encoding of every byte outside RFC 3986's unreserved set.</summary>
    Url,

    Base64,

    /// <summary>The URL- and filename-safe alphabet of RFC 4648 §5, unpadded.</summary>
    Base64Url,
}

/// <summary>Whether a codec treats each line as its own text or the input as one text.</summary>
public enum RecipeScope
{
    EachLine,
    WholeText,
}

/// <summary>
/// A text transformation the user can apply to a document, saved as one JSON file (see
/// <see cref="IRecipeStore"/>). Settings not used by <see cref="Kind"/> are carried but ignored,
/// so switching kind in the editor and back loses nothing.
/// </summary>
public sealed record Recipe
{
    /// <summary>Stands for the line in <see cref="Template"/>.</summary>
    public const string LinePlaceholder = "{line}";

    public RecipeKind Kind { get; init; } = RecipeKind.Template;

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

    public RecipeCodec Codec { get; init; } = RecipeCodec.Base64;

    public RecipeScope Scope { get; init; } = RecipeScope.WholeText;
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
