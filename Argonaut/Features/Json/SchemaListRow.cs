using Argonaut.Features.Json.Schema;

namespace Argonaut.Features.Json;

/// <summary>
/// One row of the schema list in the schema flyout: "No schema", a section heading, a catalog
/// schema - scored when it fits the document - or "Open schema folder…". Drawn the way the type
/// list's <see cref="SchemaRootPick"/> rows are, so both halves of the flyout read alike.
/// </summary>
public sealed class SchemaListRow
{
    private SchemaListRow(string name, bool isHeader, SchemaCatalogEntry? entry, string? score, bool showSeparator, bool isRecommended)
    {
        Name = name;
        IsHeader = isHeader;
        Entry = entry;
        Score = score;
        ShowSeparator = showSeparator;
        IsRecommended = isRecommended;
    }

    /// <summary>The schema's name, the heading's caption, or the action's label.</summary>
    public string Name { get; }

    public bool IsHeader { get; }

    public bool IsSelectable => !IsHeader;

    /// <summary>The schema this row binds; null for "No schema", a heading and the folder action.</summary>
    public SchemaCatalogEntry? Entry { get; }

    /// <summary>How well the schema fits, as the type list badges it; null when unscored.</summary>
    public string? Score { get; }

    public bool HasScore => Score is not null;

    /// <summary>Draws a rule above this row - set on a heading that follows other content.</summary>
    public bool ShowSeparator { get; }

    /// <summary>The schema that fits clearly enough to be bound unasked.</summary>
    public bool IsRecommended { get; }

    public static SchemaListRow Action(string label) => new(label, isHeader: false, null, null, false, false);

    public static SchemaListRow Header(string caption, bool showSeparator = false) => new(caption, isHeader: true, null, null, showSeparator, false);

    public static SchemaListRow Schema(SchemaCatalogEntry entry, SchemaRootMatch? fit = null, bool isRecommended = false)
        => new(entry.DisplayName, isHeader: false, entry, SchemaRootPick.ScoreText(fit), false, isRecommended);
}
