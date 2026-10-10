using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Preview;

namespace Argonaut.Features.Json.ArrayTable;

/// <summary>
/// One cell of the array table, shown in full beside the grid.
///
/// This is the answer to the two things a grid cell cannot do, and it is deliberately scoped to a
/// CELL rather than a row: a row's tree beside the grid would just be the JSON view with the
/// element collapsed, which is where the reader came from.
///
///   * a long scalar - the grid caps a column at <see cref="Argonaut.Ui.TableGrid.TableStructure"/>'s
///     discovered width and a cell's text at the display cap, so a long string is only ever
///     readable here;
///   * a container - its own tree, over just that cell's bytes (see <see cref="ValuePreview"/>), so
///     a 2,199-element <c>offerCSV</c> opens instantly and nothing beyond the rows on screen is held.
///
/// Cost is bounded by the one subtree on screen, whatever the file's size.
/// </summary>
public static class JsonArrayCellDetail
{
    /// <summary>
    /// Builds the detail for one value of the table's array. A container gets a tree over its own
    /// byte range; a scalar gets its text.
    /// </summary>
    public static ValuePreview ForNode(JsonArrayTableSession table, TreeNode node, string title)
    {
        if (node.IsContainer)
        {
            long end = table.Text.End(node);
            return ValuePreview.ForJson(title, table.Origin, table.ArrayOffset + node.ValueStart, end - node.ValueStart);
        }

        // Unquoted, unlike the cell: the pane is where a value is read and copied, and the
        // quoting that tells "5" from 5 has already done its job in the grid.
        bool isString = node.FormatKind == (byte)JsonTokenKind.String;
        long start = isString ? node.ValueStart + 1 : node.ValueStart;
        long length = (isString ? node.ValueEnd - 1 : node.ValueEnd) - start;
        string value = DisplayText.Read(table.Inner.Bytes, start, (int)System.Math.Min(int.MaxValue, length), out bool truncated, ValuePreview.MaxTextBytes);
        return ValuePreview.ForText(title, value, truncated);
    }
}
