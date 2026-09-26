using System;
using System.Threading.Tasks;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

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
///   * a container - its own tree, over just that cell's bytes: the same sparse index and
///     <see cref="TreeDocument"/> the JSON view uses, so a 2,199-element <c>offerCSV</c> opens
///     instantly and nothing beyond the rows on screen is held.
///
/// Cost is bounded by the one subtree on screen, whatever the file's size.
/// </summary>
public sealed class JsonArrayCellDetail : IDisposable
{
    /// <summary>
    /// Bytes of a scalar decoded for the pane. Far past the grid's own display cap - the point of
    /// the pane is to show what the cell could not - while still bounded, because a JSON string
    /// can be as large as the file.
    /// </summary>
    public const int MaxScalarBytes = 256 * 1024;

    /// <summary>Levels of a container cell opened on the click - enough to see its shape without
    /// reading a large subtree.</summary>
    private const int TreeExpandDepth = 2;

    private readonly IndexedSourceSession<JsonSparseIndex>? session;

    private JsonArrayCellDetail(string title, string? text, bool truncated, TreeDocument? tree,
        IndexedSourceSession<JsonSparseIndex>? session)
    {
        Title = title;
        Text = text;
        Truncated = truncated;
        Tree = tree;
        this.session = session;
    }

    /// <summary>Which cell this is: the column's route, and the row it came from.</summary>
    public string Title { get; }

    /// <summary>The scalar's full text, or null when this cell holds a container.</summary>
    public string? Text { get; }

    /// <summary>The scalar was longer than <see cref="MaxScalarBytes"/> and is shown cut.</summary>
    public bool Truncated { get; }

    /// <summary>The container's tree, or null when this cell holds a scalar.</summary>
    public TreeDocument? Tree { get; }

    public bool IsTree => Tree is not null;

    public bool IsText => Tree is null;

    /// <summary>
    /// Builds the detail for one value of the table's array. A container gets a tree over its own
    /// byte range, opened <see cref="TreeExpandDepth"/> levels deep; a scalar gets its text.
    /// </summary>
    public static JsonArrayCellDetail ForNode(JsonArrayTableSession table, TreeNode node, string title)
    {
        if (node.IsContainer)
        {
            long end = table.Text.End(node);
            var session = IndexedSourceSession<JsonSparseIndex>.Start(
                table.Origin.OpenRange(table.ArrayOffset + node.ValueStart, end - node.ValueStart), JsonSparseIndex.StartIndexing);
            var reader = new JsonTreeReader(session.Bytes);
            var text = new JsonTreeText(session.Bytes, session.Index.Structure, reader);
            var bytes = session.Bytes;
            var tree = new TreeDocument(session.Index.Structure, reader, new JsonTreePainter(text, hintProviders: null, offerArrayTable: false),
                new TreeExpandState(TreeExpandDepth), () => bytes.AvailableLength);
            _ = TellTreeWhenIndexedAsync(session, tree);
            return new JsonArrayCellDetail(title, text: null, truncated: false, tree, session);
        }

        // Unquoted, unlike the cell: the pane is where a value is read and copied, and the
        // quoting that tells "5" from 5 has already done its job in the grid.
        bool isString = node.FormatKind == (byte)JsonTokenKind.String;
        long start = isString ? node.ValueStart + 1 : node.ValueStart;
        long length = (isString ? node.ValueEnd - 1 : node.ValueEnd) - start;
        string value = DisplayText.Read(table.Inner.Bytes, start, (int)Math.Min(int.MaxValue, length), out bool truncated, MaxScalarBytes);

        return new JsonArrayCellDetail(title, value, truncated, tree: null, session: null);
    }

    /// <summary>The tree draws from the bytes at once; once its index is done, jumps inside it
    /// are fast too, and the surface is told so it can size its scroll range.</summary>
    private static async Task TellTreeWhenIndexedAsync(IndexedSourceSession<JsonSparseIndex> session, TreeDocument tree)
    {
        try
        {
            await session.IndexingTask;
        }
        catch
        {
            // A malformed cell still shows what can be read; the table reports the file's failure.
        }

        tree.NotifyGrew();
    }

    public void Dispose()
    {
        // Every surface lets go of the tree before the bytes it reads are released.
        Tree?.Close();
        session?.Dispose();
    }
}
