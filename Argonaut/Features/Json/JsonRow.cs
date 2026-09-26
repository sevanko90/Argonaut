using Argonaut.Features.Json.Indexing;

namespace Argonaut.Features.Json;

/// <summary>
/// What one pane of a diff row shows for its document: a member's name and a value - a scalar as
/// written, or a container's bracket or summary - coloured by kind.
/// </summary>
public sealed class JsonRow
{
    public JsonRow(long valueStart, JsonTokenKind kind, string? name, string value)
    {
        ValueStart = valueStart;
        Kind = kind;
        Name = name;
        Value = value;
    }

    /// <summary>Where the row's value starts in its document - the row's identity.</summary>
    public long ValueStart { get; }

    public JsonTokenKind Kind { get; }

    public string? Name { get; }

    public string Value { get; }

    // Scalar-kind flags consumed by JsonRowPresenter.axaml's Classes.* bindings for per-type
    // value colouring. Containers match none of them and keep the default foreground.
    public bool IsStringValue => Kind == JsonTokenKind.String;
    public bool IsNumberValue => Kind == JsonTokenKind.Number;
    public bool IsBooleanValue => Kind is JsonTokenKind.True or JsonTokenKind.False;
    public bool IsNullValue => Kind == JsonTokenKind.Null;
}
