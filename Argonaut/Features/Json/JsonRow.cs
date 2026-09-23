using Argonaut.Features.Json.Indexing;

namespace Argonaut.Features.Json;

/// <summary>
/// Display model for one visible row: either a real token (value, or container start
/// shown collapsed/expanded) or a synthetic "N more items" placeholder for a container
/// whose direct-child count exceeds the display cap.
/// </summary>
public sealed class JsonRow
{
    public JsonRow(int position, int tokenIndex, int depth, JsonTokenKind kind, string? name, string value, bool hasChildren, bool isExpanded, bool isPlaceholder, string? hint = null, string? truncationHint = null, long? truncatedValueOffset = null, int? arrayIndex = null, string? schemaTitle = null, string? schemaDescription = null, string? schemaLabel = null)
    {
        SchemaTitle = schemaTitle;
        SchemaDescription = schemaDescription;
        SchemaLabel = schemaLabel;
        Position = position;
        TokenIndex = tokenIndex;
        Depth = depth;
        Kind = kind;
        Name = name;
        Value = value;
        HasChildren = hasChildren;
        IsExpanded = isExpanded;
        IsPlaceholder = isPlaceholder;
        Hint = hint;
        TruncationHint = truncationHint;
        TruncatedValueOffset = truncatedValueOffset;
        ArrayIndex = arrayIndex;
    }

    /// <summary>Index into the owning JsonVisibleRowCollection's current visible list.</summary>
    public int Position { get; }
    public int TokenIndex { get; }
    public int Depth { get; }
    public JsonTokenKind Kind { get; }
    public string? Name { get; }
    public string Value { get; }
    public bool HasChildren { get; }
    public bool IsExpanded { get; }
    public bool IsPlaceholder { get; }

    /// <summary>Zero-based position among this row's array siblings, or null when its
    /// parent isn't an array (an object member, or the document root) - drives the small
    /// index label shown to the left of the expander for array elements only.</summary>
    public int? ArrayIndex { get; }

    /// <summary>Muted decoded-value hint (e.g. a decoded date) to render after Value, or null.</summary>
    public string? Hint { get; }

    /// <summary>The bound schema's <c>title</c> for this row (or an enum member's label), or null
    /// when the schema documents no title. This is the *real* title only - the description
    /// fallback lives on <see cref="SchemaLabel"/> - so the tooltip can tell "titled" apart from
    /// "described" and only draw its title/description separator when there genuinely are
    /// both.</summary>
    public string? SchemaTitle { get; }

    /// <summary>The schema's <c>description</c> for this row, shown under the title in the schema
    /// gutter's tooltip. Null when the schema documents no description.</summary>
    public string? SchemaDescription { get; }

    /// <summary>What the schema gutter renders on this row: <see cref="SchemaTitle"/>, falling
    /// back to the first line of <see cref="SchemaDescription"/> for the (common) generated-schema
    /// case of a described-but-untitled property. Null when the schema says nothing here, which is
    /// also what blanks the gutter cell. Kept separate from <see cref="Hint"/> so a row can carry
    /// both a decoded date and a schema label.</summary>
    public string? SchemaLabel { get; }

    /// <summary>Drives the tooltip's title/description separator: only a row carrying both needs
    /// a rule between them.</summary>
    public bool HasSchemaTitleAndDescription => SchemaTitle is not null && SchemaDescription is not null;

    /// <summary>Whether this row opens a container, so the schema gutter can render its label as a
    /// heading over the child labels indented beneath it. Excludes placeholder rows, which borrow
    /// their container's <see cref="Kind"/> but describe a display cap rather than the container
    /// itself (they carry no schema label either way, so this only guards against later misuse).
    /// Closing-bracket rows are <c>EndObject</c>/<c>EndArray</c> and so are excluded already.</summary>
    public bool IsContainerRow => !IsPlaceholder && Kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    /// <summary>
    /// Whether this row offers the "view as table" link: a non-empty array, whatever its
    /// elements are. An array of objects is the case the feature was built for, but an array of
    /// scalars tables perfectly well as one column - and reshaping a flat array into N columns
    /// is the whole point of the second column mode - so the link is not restricted to elements
    /// of any particular shape. An EMPTY array is excluded: there is nothing to show, and the
    /// link would be a dead end.
    /// </summary>
    public bool CanViewAsTable => IsContainerRow && Kind == JsonTokenKind.StartArray && HasChildren;

    /// <summary>Muted note that Name and/or Value was display-capped (with the full length), or null.</summary>
    public string? TruncationHint { get; }

    /// <summary>Byte offset of the overflowing value's content in the file, set only when
    /// Value (not just Name) was truncated - lets the view offer a "view in raw" jump to
    /// where the full value actually starts. Null otherwise.</summary>
    public long? TruncatedValueOffset { get; }

    // Scalar-kind flags consumed by JsonView.axaml Classes.* bindings for per-type value
    // coloring. Container, placeholder and summary rows match none of them and keep the
    // default foreground.
    public bool IsStringValue => Kind == JsonTokenKind.String;
    public bool IsNumberValue => Kind == JsonTokenKind.Number;
    public bool IsBooleanValue => Kind is JsonTokenKind.True or JsonTokenKind.False;
    public bool IsNullValue => Kind == JsonTokenKind.Null;

    // Split for JsonView.axaml: TruncationHint alone can't tell a plain informational note
    // (name-only truncation, nothing to jump to) apart from one that should render as a
    // clickable "view in raw" link (value truncation, which always carries an offset).
    public bool ShowPlainTruncationHint => TruncationHint is not null && TruncatedValueOffset is null;
    public bool ShowTruncationLink => TruncatedValueOffset is not null;
}
