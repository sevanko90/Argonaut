using System;
using System.Collections.Generic;
using System.Text;

namespace Argonaut.Features.Json;

/// <summary>
/// One step from an element towards a column's value: a property name, or a position in an
/// array. Names are held as raw UTF-8 because that is what they are matched against - the bytes
/// in the mapping - so realizing a row decodes nothing.
/// </summary>
/// <param name="Name">The property name's raw UTF-8, or null when this step is an array
/// position.</param>
/// <param name="Index">The array position, or -1 when this step is a property name.</param>
public readonly record struct RouteStep(byte[]? Name, int Index)
{
    public bool IsIndex => Name is null;

    public static RouteStep Property(string name) => new(Encoding.UTF8.GetBytes(name), -1);

    public static RouteStep At(int index) => new(null, index);
}

/// <summary>
/// Where one column's value lives inside an element, and what its header says. The steps are the
/// column's identity - two columns are the same column when they take the same route, whatever
/// their headers read - and <see cref="Display"/> is only ever shown.
///
/// A top-level column is one step (<c>id</c>); expanding a container appends one
/// (<c>geometry.type</c>, <c>bbox[0]</c>). Nothing here recurses on its own: a route exists
/// because something asked for it.
/// </summary>
public sealed record ColumnRoute(IReadOnlyList<RouteStep> Steps, string Display)
{
    /// <summary>A direct property of the element - the only shape column discovery produces
    /// until a header expands something.</summary>
    public static ColumnRoute Property(string name) => new([RouteStep.Property(name)], name);
}

/// <summary>
/// The set of routes the grid's columns take, as one node per container that something is
/// expanded inside of - so it is as small as what has been expanded, not a copy of the
/// document's shape, and an array of objects with nothing expanded is a single node.
///
/// This is what keeps a realized row bounded. Realizing a row walks an element's direct children
/// exactly as it always did and asks the node it is standing on about each one:
///
///   * no match, or a match with no <c>inner</c> - the child is skipped whole via
///     <c>EndIndex + 1</c>, which is the cost the table has always paid;
///   * a match with an <c>inner</c> node - the walk descends into that child, and only that
///     child.
///
/// So collapsed columns cost nothing beyond the element's own children, and total work per row
/// is that plus the tokens inside explicitly expanded subtrees. Depth grows only because someone
/// clicked.
///
/// Matching is a linear scan of the level's entries - the same scan the property-name lookup it
/// replaces did, against the same raw UTF-8 - because a level holds one entry per column drawn
/// at it, which is tens at most (the grid caps its column count long before a scan matters).
/// </summary>
public sealed class ExpandedRoutes
{
    /// <summary>No columns at all - what the reshape modes use, where a cell is a whole element
    /// and there is no route to take.</summary>
    public static ExpandedRoutes None { get; } = new([]);

    private readonly Entry[] entries;

    private ExpandedRoutes(Entry[] entries) => this.entries = entries;

    /// <param name="Column">Column this step's value is drawn in, or -1 when the step is only
    /// passed through on the way to something deeper.</param>
    /// <param name="Inner">The level to descend into, or null when nothing below this step is
    /// expanded.</param>
    private readonly record struct Entry(RouteStep Step, int Column, ExpandedRoutes? Inner);

    public bool IsEmpty => this.entries.Length == 0;

    /// <summary>
    /// Builds the levels for a set of columns, in the order given - column <c>c</c> is
    /// <c>routes[c]</c>, which is the index its cells are written at.
    /// </summary>
    public static ExpandedRoutes Build(IReadOnlyList<ColumnRoute> routes)
    {
        if (routes.Count == 0)
            return None;

        var root = new Level();
        for (int c = 0; c < routes.Count; c++)
            root.Add(routes[c].Steps, depth: 0, column: c);

        return root.Freeze();
    }

    /// <summary>The flat, one-step-per-column shape discovery produces for an array of objects:
    /// column <c>c</c> is the direct property <c>names[c]</c>, nothing expanded.</summary>
    public static ExpandedRoutes ForProperties(IReadOnlyList<string> names)
    {
        var routes = new ColumnRoute[names.Count];
        for (int c = 0; c < names.Count; c++)
            routes[c] = ColumnRoute.Property(names[c]);

        return Build(routes);
    }

    /// <summary>Matches a child by its property name, raw. False means this level draws nothing
    /// from that child and nothing below it is expanded either - so the caller skips its whole
    /// subtree.</summary>
    public bool TryMatchName(ReadOnlySpan<byte> name, out int column, out ExpandedRoutes? inner)
    {
        foreach (var entry in this.entries)
        {
            if (entry.Step.Name is { } candidate && name.SequenceEqual(candidate))
            {
                column = entry.Column;
                inner = entry.Inner;
                return true;
            }
        }

        column = -1;
        inner = null;
        return false;
    }

    /// <summary>Matches a child by its position in the enclosing array - the counterpart of
    /// <see cref="TryMatchName"/> for elements of an expanded array.</summary>
    public bool TryMatchIndex(int index, out int column, out ExpandedRoutes? inner)
    {
        foreach (var entry in this.entries)
        {
            if (entry.Step.IsIndex && entry.Step.Index == index)
            {
                column = entry.Column;
                inner = entry.Inner;
                return true;
            }
        }

        column = -1;
        inner = null;
        return false;
    }

    /// <summary>Mutable while the routes are being laid out; <see cref="Freeze"/> is what the
    /// row collection ever sees.</summary>
    private sealed class Level
    {
        private readonly List<RouteStep> steps = [];
        private readonly List<int> columns = [];
        private readonly List<Level?> inner = [];

        public void Add(IReadOnlyList<RouteStep> steps, int depth, int column)
        {
            var step = steps[depth];
            int slot = Slot(step);
            if (slot < 0)
            {
                this.steps.Add(step);
                this.columns.Add(-1);
                this.inner.Add(null);
                slot = this.steps.Count - 1;
            }

            if (depth == steps.Count - 1)
            {
                // A container drawn as a summary AND expanded is not a shape the headers can
                // produce - expanding replaces the column - but taking the last writer keeps
                // Build total rather than throwing on a caller's behalf.
                this.columns[slot] = column;
                return;
            }

            this.inner[slot] ??= new Level();
            this.inner[slot]!.Add(steps, depth + 1, column);
        }

        public ExpandedRoutes Freeze()
        {
            var frozen = new Entry[this.steps.Count];
            for (int i = 0; i < frozen.Length; i++)
                frozen[i] = new Entry(this.steps[i], this.columns[i], this.inner[i]?.Freeze());

            return new ExpandedRoutes(frozen);
        }

        private int Slot(RouteStep step)
        {
            for (int i = 0; i < this.steps.Count; i++)
            {
                var candidate = this.steps[i];
                bool same = step.IsIndex
                    ? candidate.IsIndex && candidate.Index == step.Index
                    : candidate.Name is { } name && name.AsSpan().SequenceEqual(step.Name);

                if (same)
                    return i;
            }

            return -1;
        }
    }
}

/// <summary>
/// What the sampled elements held in one column, recorded by the discovery walk that was already
/// visiting those tokens. Free: a container's child count is a walk the badge text needs anyway
/// (capped), and its byte size is two O(1) token unpacks.
///
/// It exists to answer two questions the header will ask - whether this column has anything
/// inside it to expand, and how many index columns expanding an array would draw - and one the
/// defaults ask: whether a column is small and uniform enough to open expanded.
/// </summary>
/// <param name="HasObjects">The sample saw an object in this column.</param>
/// <param name="HasArrays">The sample saw an array in this column.</param>
/// <param name="WidestArity">Most elements seen in an array in this column, counted up to
/// <see cref="ColumnNesting.ArityCap"/> and no further - past the cap the number stops being an
/// expansion width and starts being a reason not to expand.</param>
/// <param name="LargestBytes">Bytes spanned by the largest container seen in this column,
/// opening brace through closing brace.</param>
public readonly record struct ColumnNesting(bool HasObjects, bool HasArrays, int WidestArity, long LargestBytes)
{
    /// <summary>Counting stops here. Comfortably above any array width worth drawing as columns,
    /// so it bounds the discovery walk without ever truncating an answer that would be used.</summary>
    public const int ArityCap = 16;

    /// <summary>Nothing nested was seen - a column of scalars, which gets no expand affordance.</summary>
    public static ColumnNesting Scalar => default;

    public bool CanExpand => HasObjects || HasArrays;

    public ColumnNesting WithObject(long bytes)
        => this with { HasObjects = true, LargestBytes = Math.Max(LargestBytes, bytes) };

    public ColumnNesting WithArray(int arity, long bytes)
        => this with { HasArrays = true, WidestArity = Math.Max(WidestArity, arity), LargestBytes = Math.Max(LargestBytes, bytes) };
}
