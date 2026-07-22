using Argonaut.Features.Json;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the JsonRow scalar-kind flags backing the per-type value coloring in
/// JsonView.axaml: each scalar kind raises exactly its own flag, and container kinds
/// (also used by "N more items" placeholder rows) raise none.
/// </summary>
public class JsonRowKindFlagTests
{
    private static JsonRow MakeRow(JsonTokenKind kind) =>
        new(position: 0, tokenIndex: 0, depth: 0, kind, name: null, value: "x",
            hasChildren: false, isExpanded: false, isPlaceholder: false);

    [Theory]
    [InlineData(JsonTokenKind.String, true, false, false, false)]
    [InlineData(JsonTokenKind.Number, false, true, false, false)]
    [InlineData(JsonTokenKind.True, false, false, true, false)]
    [InlineData(JsonTokenKind.False, false, false, true, false)]
    [InlineData(JsonTokenKind.Null, false, false, false, true)]
    [InlineData(JsonTokenKind.StartObject, false, false, false, false)]
    [InlineData(JsonTokenKind.EndObject, false, false, false, false)]
    [InlineData(JsonTokenKind.StartArray, false, false, false, false)]
    [InlineData(JsonTokenKind.EndArray, false, false, false, false)]
    public void ScalarKindFlags_MatchKind(JsonTokenKind kind, bool isString, bool isNumber, bool isBoolean, bool isNull)
    {
        var row = MakeRow(kind);

        Assert.Equal(isString, row.IsStringValue);
        Assert.Equal(isNumber, row.IsNumberValue);
        Assert.Equal(isBoolean, row.IsBooleanValue);
        Assert.Equal(isNull, row.IsNullValue);
    }
}
