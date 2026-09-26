using System;
using System.Collections.Specialized;
using Argonaut.Engine.Collections;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents;
using Argonaut.Ui.TableGrid;

namespace Argonaut.Features.Json.ArrayTable;

/// <summary>How a JSON array's elements are laid out across the table's columns.</summary>
public enum JsonArrayColumnMode
{
    /// <summary>
    /// One row per element, one column per property name. The natural reading of
    /// <c>[ { … }, { … } ]</c>.
    /// </summary>
    ByProperty,

    /// <summary>
    /// One row per N elements, filled left to right - for a flat array that is really
    /// n-dimensional data flattened into one sequence (<c>[x0, y0, x1, y1, …]</c>, RGB triples,
    /// a fixed-width record with no object wrapper). N is the structure's column count, chosen
    /// by the user; there is no right answer to validate against, because the shape is something
    /// the user knows and the JSON does not encode.
    /// </summary>
    Reshape
}

/// <summary>
/// The table's ItemsSource: <see cref="TableRow"/>s produced on demand from a JSON array,
/// so the CSV grid's presentation layer renders them unchanged.
///
/// It keeps no walk state of its own. <see cref="VirtualizingItemsSourceBase.Count"/> derives from
/// <see cref="JsonArrayElements.ElementCount"/>, and realizing a row is a
/// <see cref="JsonArrayElements.ElementAt"/> lookup per element the row covers - one in
/// <see cref="JsonArrayColumnMode.ByProperty"/> mode (plus a bounded read of that element's
/// direct children, and of the children of whatever has been expanded inside it), N in
/// <see cref="JsonArrayColumnMode.Reshape"/> mode - and nothing else.
/// Reshape is therefore a pure re-chunking of the same per-element decode, not a second
/// value-reading path.
///
/// It invents no columns: <see cref="TableStructure"/> and the <see cref="ExpandedRoutes"/> that
/// say where each of them lives inside an element both arrive finished, from whoever discovered
/// the property names or chose the reshape width.
/// </summary>
public sealed class JsonArrayRowCollection : VirtualizingItemsSourceBase, IColumnFitSource
{
    private const int CacheCapacity = 1000;

    /// <summary>Slower than CSV's 120ms: rows here become addressable a checkpoint's worth at a
    /// time, not one at a time.</summary>
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly JsonArrayElements elements;
    private readonly JsonTreeReader reader;
    private readonly JsonTreeText text;
    private readonly LruCache<int, TableRow> cache = new(CacheCapacity);

    private TableStructure structure;
    private JsonArrayColumnMode mode;

    // Where each column's value sits inside an element. Property names are matched RAW (escapes
    // and all) against the mapping, which is exactly what the columns were discovered from and
    // what the tree shows for the same token - so the two never disagree about which property a
    // cell belongs to - and no name is ever decoded to realize a row.
    private ExpandedRoutes routes;

    private IndexGrowthMonitor? growthMonitor;
    private int notifiedCount;

    public JsonArrayRowCollection(JsonArrayElements elements, JsonTreeReader reader, JsonTreeText text,
        TableStructure structure, ExpandedRoutes routes, JsonArrayColumnMode mode)
    {
        this.elements = elements;
        this.reader = reader;
        this.text = text;
        this.structure = structure;
        this.routes = routes;
        this.mode = mode;
        // Sampled before the count snapshot: a walk that finishes in the window between the
        // snapshot and a check made after it would leave this collection with no monitor,
        // permanently reporting the element count it happened to see here.
        bool walkWasRunning = !elements.AllItemsPublished;

        this.notifiedCount = GetCount();

        if (walkWasRunning)
            StartGrowthMonitor();
    }

    protected override int GetCount()
    {
        int count = elements.ElementCount;
        if (mode == JsonArrayColumnMode.ByProperty)
            return count;

        int columns = Math.Max(1, structure.ColumnCount);

        // While the array is still growing, a trailing PARTIAL row would gain cells on the next
        // tick - and a row that changes rather than appears cannot be published as an Add.
        // Publishing only whole rows until the walk completes keeps growth a pure append; the
        // final refresh (which the growth monitor guarantees) brings the last partial row in.
        return elements.AllItemsPublished ? (count + columns - 1) / columns : count / columns;
    }

    protected override object GetItem(int index) => GetRow(index);

    /// <summary>
    /// Re-shapes the grid: new columns, and possibly a new mode. Which cell a value lands in
    /// changes - in reshape mode so does which row - so the cache is dropped and a Reset
    /// re-realizes what is visible. This never re-walks the array: element addressing is
    /// independent of how the columns are drawn.
    /// </summary>
    public void SetShape(TableStructure newStructure, ExpandedRoutes newRoutes, JsonArrayColumnMode newMode)
    {
        if (ReferenceEquals(structure, newStructure) && ReferenceEquals(routes, newRoutes) && mode == newMode)
            return;

        structure = newStructure;
        routes = newRoutes;
        mode = newMode;
        cache.Clear();
        notifiedCount = GetCount();
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// The widest text this column has in the realized-row cache - what a fit-to-content
    /// double-click on its resizer measures. Deliberately bounded to what has already been
    /// decoded: the true widest value in a multi-gigabyte array is a full scan away, and the
    /// cache is a free sample of exactly the rows the user has been looking at.
    /// </summary>
    public int LongestRealizedText(int columnIndex)
    {
        if (columnIndex < 0)
            return 0;

        int longest = 0;
        foreach (var row in cache.Values)
        {
            if (columnIndex < row.Cells.Count)
                longest = Math.Max(longest, row.Cells[columnIndex].Text.Length);
        }

        return longest;
    }

    private TableRow GetRow(int i)
    {
        if (cache.TryGetValue(i, out var cached))
            return cached;

        int count = Count;
        if (i < 0 || i >= count)
            return new TableRow(i + 1, []);

        var row = new TableRow(i + 1, mode == JsonArrayColumnMode.ByProperty ? ByPropertyCells(i) : ReshapeCells(i));
        cache.Set(i, row);
        return row;
    }

    /// <summary>
    /// The value one cell came from, or null when the cell is empty - an element missing that
    /// property, or a position past the end of a short row.
    ///
    /// Reads the element again rather than remembering a value per realized cell: a click is one
    /// bounded read, whereas the cache holds a thousand rows and would carry a node per column of
    /// every one of them for a lookup almost none of them are ever asked for.
    /// </summary>
    public TreeNode? NodeForCell(int rowIndex, int column)
    {
        if (column < 0 || rowIndex < 0 || rowIndex >= Count)
            return null;

        if (mode == JsonArrayColumnMode.Reshape)
        {
            int first = rowIndex * Math.Max(1, structure.ColumnCount) + column;
            return first < elements.ElementCount ? elements.ElementAt(first) : null;
        }

        var element = elements.ElementAt(rowIndex);
        if (element.FormatKind != (byte)JsonTokenKind.StartObject)
            return column == 0 ? element : null;

        return NodeIn(routes, element, column);
    }

    /// <summary>The same descent <see cref="FillFrom"/> makes, stopping at one column.</summary>
    private TreeNode? NodeIn(ExpandedRoutes level, TreeNode container, int wanted)
    {
        long position = reader.FirstChildPosition(container.ValueStart);
        for (int ordinal = 0; reader.TryReadChild(container.FormatKind, ref position, out var child, out _); ordinal++)
        {
            if (Match(level, container, child, ordinal, out int column, out var inner))
            {
                if (column == wanted)
                    return child;

                if (inner is not null && child.IsContainer && NodeIn(inner, child, wanted) is { } found)
                    return found;
            }

            position = text.End(child);
            if (position == long.MaxValue)
                break;
        }

        return null;
    }

    /// <summary>
    /// One element across the property columns. A non-object element (a scalar, a nested array -
    /// a ragged array is data, not an error) has no properties to distribute, so it renders as a
    /// single cell in the first column; that is also the shape a whole array of scalars takes,
    /// where discovery produced one "value" column to begin with.
    /// </summary>
    private TableCell[] ByPropertyCells(int rowIndex)
    {
        var element = elements.ElementAt(rowIndex);

        if (element.FormatKind != (byte)JsonTokenKind.StartObject)
            return [new TableCell(text.ValueText(element))];

        var cells = new TableCell[structure.ColumnCount];
        for (int c = 0; c < cells.Length; c++)
            cells[c] = new TableCell(string.Empty);

        FillFrom(cells, routes, element);
        return cells;
    }

    /// <summary>
    /// Draws one container's children into the cells the given level of the routes asks for, and
    /// descends into exactly those children something is expanded inside of.
    ///
    /// Everything else is stepped over whole, which is why an unexpanded grid costs the element's
    /// direct children, once. Recursion depth is the depth someone expanded to, not the
    /// document's.
    /// </summary>
    private void FillFrom(TableCell[] cells, ExpandedRoutes level, TreeNode container)
    {
        long position = reader.FirstChildPosition(container.ValueStart);
        for (int ordinal = 0; reader.TryReadChild(container.FormatKind, ref position, out var child, out _); ordinal++)
        {
            if (Match(level, container, child, ordinal, out int column, out var inner))
            {
                if (column >= 0 && column < cells.Length)
                    cells[column] = new TableCell(text.ValueText(child));

                if (inner is not null && child.IsContainer)
                    FillFrom(cells, inner, child);
            }

            position = text.End(child);
            if (position == long.MaxValue)
                break;
        }
    }

    /// <summary>Asks a level of the routes about one child: by its raw name in an object, by its
    /// position in an array.</summary>
    private bool Match(ExpandedRoutes level, TreeNode container, TreeNode child, int ordinal, out int column, out ExpandedRoutes? inner)
        => container.FormatKind == (byte)JsonTokenKind.StartObject
            ? level.TryMatchName(text.NameBytes(child), out column, out inner)
            : level.TryMatchIndex(ordinal, out column, out inner);

    /// <summary>
    /// N consecutive elements across N columns, row-major - the same walk order as the array
    /// itself. A partial last row simply yields fewer cells than the header has columns; each
    /// cell carries its own width, so nothing misaligns and no padding is needed.
    /// </summary>
    private TableCell[] ReshapeCells(int rowIndex)
    {
        int columns = Math.Max(1, structure.ColumnCount);
        int first = rowIndex * columns;
        int available = Math.Min(columns, elements.ElementCount - first);
        if (available <= 0)
            return [];

        var cells = new TableCell[available];
        for (int c = 0; c < available; c++)
            cells[c] = new TableCell(text.ValueText(elements.ElementAt(first + c)));

        return cells;
    }

    private void StartGrowthMonitor()
    {
        growthMonitor = new IndexGrowthMonitor(GrowthPollInterval, elements.IndexingTask,
            isComplete: () => elements.AllItemsPublished,
            refresh: NotifyGrowth);
    }

    private void NotifyGrowth()
    {
        if (IsDisposed)
            return;

        int current = GetCount();
        if (current <= notifiedCount)
            return;

        // Placeholder entries only - the panel re-queries through the indexer when it actually
        // realizes a row, so decoding every new row here (millions, between ticks, on a large
        // array) would defeat the point of virtualizing at all.
        var added = new object?[current - notifiedCount];
        int startingIndex = notifiedCount;
        notifiedCount = current;

        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, startingIndex));
    }

    protected override void DisposeCore()
    {
        growthMonitor?.Dispose();
        growthMonitor = null;
        cache.Clear();
    }
}
