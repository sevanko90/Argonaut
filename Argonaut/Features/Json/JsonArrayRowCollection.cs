using System;
using System.Collections.Specialized;
using Argonaut.Features.Csv;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

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
/// The table's ItemsSource: <see cref="CsvVisibleRow"/>s produced on demand from a JSON array,
/// so the CSV grid's presentation layer renders them unchanged.
///
/// It keeps no walk state of its own. <see cref="MemoryMappedCollectionBase.Count"/> derives from
/// <see cref="JsonArrayElementIndex.ElementCount"/>, and realizing a row is a
/// <see cref="JsonArrayElementIndex.TokenForElement"/> lookup per element the row covers - one in
/// <see cref="JsonArrayColumnMode.ByProperty"/> mode (plus a bounded read of that element's
/// direct children, and of the children of whatever has been expanded inside it), N in
/// <see cref="JsonArrayColumnMode.Reshape"/> mode - and nothing else.
/// Reshape is therefore a pure re-chunking of the same per-element decode, not a second
/// value-reading path.
///
/// It invents no columns: <see cref="CsvStructure"/> and the <see cref="ExpandedRoutes"/> that
/// say where each of them lives inside an element both arrive finished, from whoever discovered
/// the property names or chose the reshape width.
/// </summary>
public sealed class JsonArrayRowCollection : MemoryMappedCollectionBase, IColumnFitSource
{
    private const int CacheCapacity = 1000;

    /// <summary>Matches JsonVisibleRowCollection's cadence rather than CSV's 120ms: rows here
    /// arrive in stride-sized batches from the element index, not one at a time.</summary>
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly JsonArrayElementIndex elements;
    private readonly JsonStructureIndex index;
    private readonly MMapFile mmap;
    private readonly JsonRowFactory rowFactory;
    private readonly LruCache<int, CsvVisibleRow> cache = new(CacheCapacity);

    private CsvStructure structure;
    private JsonArrayColumnMode mode;

    // Where each column's value sits inside an element. Property names are matched RAW (escapes
    // and all) against the mapping, which is exactly what the columns were discovered from and
    // what the tree shows for the same token - so the two never disagree about which property a
    // cell belongs to - and no name is ever decoded to realize a row.
    private ExpandedRoutes routes;

    private IndexGrowthMonitor? growthMonitor;
    private int notifiedCount;

    public JsonArrayRowCollection(JsonArrayElementIndex elements, JsonStructureIndex index, MMapFile mmap,
        CsvStructure structure, ExpandedRoutes routes, JsonArrayColumnMode mode)
    {
        this.elements = elements;
        this.index = index;
        this.mmap = mmap;
        this.rowFactory = new JsonRowFactory(index, mmap, hintProviders: null);
        this.structure = structure;
        this.routes = routes;
        this.mode = mode;
        // Sampled before the count snapshot, for the reason JsonDiffRowCollection's constructor
        // states: a walk that finishes in the window between the snapshot and a check made
        // after it would leave this collection with no monitor, permanently reporting the
        // element count it happened to see here.
        bool walkWasRunning = !elements.IsComplete;

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
        return elements.IsComplete ? (count + columns - 1) / columns : count / columns;
    }

    protected override object GetItem(int index) => GetRow(index);

    /// <summary>
    /// Re-shapes the grid: new columns, and possibly a new mode. Which cell a value lands in
    /// changes - in reshape mode so does which row - so the cache is dropped and a Reset
    /// re-realizes what is visible. This never re-walks the array: element addressing is
    /// independent of how the columns are drawn.
    /// </summary>
    public void SetShape(CsvStructure newStructure, ExpandedRoutes newRoutes, JsonArrayColumnMode newMode)
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

    private CsvVisibleRow GetRow(int i)
    {
        if (cache.TryGetValue(i, out var cached))
            return cached;

        int count = Count;
        if (i < 0 || i >= count)
            return new CsvVisibleRow(i + 1, []);

        var row = new CsvVisibleRow(i + 1, mode == JsonArrayColumnMode.ByProperty ? ByPropertyCells(i) : ReshapeCells(i));
        cache.Set(i, row);
        return row;
    }

    /// <summary>
    /// One element across the property columns. A non-object element (a scalar, a nested array -
    /// a ragged array is data, not an error) has no properties to distribute, so it renders as a
    /// single cell in the first column; that is also the shape a whole array of scalars takes,
    /// where discovery produced one "value" column to begin with.
    /// </summary>
    private CsvCell[] ByPropertyCells(int rowIndex)
    {
        int token = elements.TokenForElement(rowIndex);
        var element = index.GetToken(token);

        if (element.Kind != JsonTokenKind.StartObject)
            return [new CsvCell(TextFor(token, element))];

        var cells = new CsvCell[structure.ColumnCount];
        for (int c = 0; c < cells.Length; c++)
            cells[c] = new CsvCell(string.Empty);

        FillFrom(cells, routes, token, element);
        return cells;
    }

    /// <summary>
    /// Draws one container's children into the cells the given level of the routes asks for, and
    /// descends into exactly those children something is expanded inside of.
    ///
    /// Everything else is skipped whole via <c>EndIndex + 1</c>, which is why an unexpanded grid
    /// costs precisely what it did before columns became routes: the element's direct children,
    /// once. Recursion depth is the depth someone expanded to, not the document's.
    ///
    /// Safe to read EndIndex here without a wait: every element the element index published has
    /// closed, so its whole subtree has too.
    /// </summary>
    private void FillFrom(CsvCell[] cells, ExpandedRoutes level, int containerToken, JsonTokenInfo container)
    {
        int ordinal = 0;
        for (int child = containerToken + 1; child < container.EndIndex; ordinal++)
        {
            var info = index.GetToken(child);
            bool isContainer = IsContainer(info.Kind);

            bool matched = info.NameLength >= 0
                ? level.TryMatchName(mmap.GetSpan(info.NameOffset, info.NameLength), out int column, out var inner)
                : level.TryMatchIndex(ordinal, out column, out inner);

            if (matched)
            {
                if (column >= 0 && column < cells.Length)
                    cells[column] = new CsvCell(TextFor(child, info));

                if (inner is not null && isContainer)
                    FillFrom(cells, inner, child, info);
            }

            child = isContainer ? info.EndIndex + 1 : child + 1;
        }
    }

    /// <summary>
    /// N consecutive elements across N columns, row-major - the same walk order as the array
    /// itself. A partial last row simply yields fewer cells than the header has columns; each
    /// cell carries its own width, so nothing misaligns and no padding is needed.
    /// </summary>
    private CsvCell[] ReshapeCells(int rowIndex)
    {
        int columns = Math.Max(1, structure.ColumnCount);
        int first = rowIndex * columns;
        int available = Math.Min(columns, elements.ElementCount - first);
        if (available <= 0)
            return [];

        var cells = new CsvCell[available];
        for (int c = 0; c < available; c++)
        {
            int token = elements.TokenForElement(first + c);
            cells[c] = new CsvCell(TextFor(token, index.GetToken(token)));
        }

        return cells;
    }

    /// <summary>
    /// The same text the tree shows for the same token, quotes included: <c>"5"</c> and <c>5</c>
    /// are different data, and a table that hides the difference is lying about the document.
    /// </summary>
    private string TextFor(int tokenIndex, JsonTokenInfo token)
        => IsContainer(token.Kind)
            ? rowFactory.BuildContainerSummary(tokenIndex, token, expanded: false)
            : rowFactory.BuildScalarText(token, out _);

    private void StartGrowthMonitor()
    {
        growthMonitor = new IndexGrowthMonitor(GrowthPollInterval, elements.IndexingTask,
            isComplete: () => elements.IsComplete,
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

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;
}
