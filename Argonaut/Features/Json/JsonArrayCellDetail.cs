using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// One cell of the array table, shown in full beside the grid.
///
/// This is the answer to the two things a grid cell cannot do, and it is deliberately scoped to a
/// CELL rather than a row: a row's tree beside the grid would just be the JSON view with the
/// element collapsed, which is where the reader came from.
///
///   * a long scalar - the grid caps a column at <see cref="Csv.CsvStructure"/>'s discovered
///     width and a cell's text at <see cref="JsonRowFactory.MaxDisplayTextLength"/>, so a long
///     string is only ever readable here;
///   * a container - rendered by the ordinary tree machinery
///     (<see cref="JsonVisibleRowCollection"/>) rooted at that cell's token, so it virtualizes
///     exactly as the JSON view does and a 2,199-element <c>offerCSV</c> opens instantly.
///
/// Cost is bounded by the one subtree on screen, whatever the file's size - which is what makes
/// this affordable where widening a column or expanding thousands of positions is not.
/// </summary>
public sealed class JsonArrayCellDetail : IDisposable
{
    /// <summary>
    /// Bytes of a scalar decoded for the pane. Far past the grid's own display cap - the point of
    /// the pane is to show what the cell could not - while still bounded, because a JSON string
    /// can be as large as the file.
    /// </summary>
    public const int MaxScalarBytes = 256 * 1024;

    private JsonArrayCellDetail(string title, string? text, bool truncated, JsonVisibleRowCollection? rows)
    {
        Title = title;
        Text = text;
        Truncated = truncated;
        Rows = rows;
    }

    /// <summary>Which cell this is: the column's route, and the row it came from.</summary>
    public string Title { get; }

    /// <summary>The scalar's full text, or null when this cell holds a container.</summary>
    public string? Text { get; }

    /// <summary>The scalar was longer than <see cref="MaxScalarBytes"/> and is shown cut.</summary>
    public bool Truncated { get; }

    /// <summary>The container's tree, or null when this cell holds a scalar.</summary>
    public JsonVisibleRowCollection? Rows { get; }

    public bool IsTree => Rows is not null;

    public bool IsText => Rows is null;

    /// <summary>
    /// Builds the detail for one token of <paramref name="index"/>. A container gets a tree opened
    /// two levels deep - enough to see the shape without walking a large subtree on the click -
    /// and a scalar gets its text.
    /// </summary>
    public static JsonArrayCellDetail ForToken(JsonStructureIndex index, IByteSource file, int tokenIndex, string title)
    {
        var token = index.GetToken(tokenIndex);

        if (token.Kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray)
        {
            return new JsonArrayCellDetail(title, text: null, truncated: false,
                new JsonVisibleRowCollection(index, file, hintProviders: null, defaultExpandDepth: 2,
                    rootTokenIndex: tokenIndex));
        }

        // Unquoted, unlike the cell: the pane is where a value is read and copied, and the
        // quoting that tells "5" from 5 has already done its job in the grid.
        string text = DisplayText.Read(file, token.Offset, token.Length, out bool truncated, MaxScalarBytes);

        return new JsonArrayCellDetail(title, text, truncated, rows: null);
    }

    public void Dispose() => Rows?.Dispose();
}
