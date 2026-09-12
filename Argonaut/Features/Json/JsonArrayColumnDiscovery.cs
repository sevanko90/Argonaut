using System;
using System.Collections.Generic;
using Argonaut.Features.Csv;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// One clickable piece of a column header. The pieces before the last are the containers the
/// column's value sits inside, and clicking one collapses back to it; the last is the value's own
/// name, clickable only when there is something inside it to open.
/// </summary>
/// <param name="Text">What this piece reads, separator included - <c>geometry</c>, <c>.type</c>,
/// <c>[0]</c>.</param>
/// <param name="Key">The container this piece toggles, or null when the piece is not clickable.</param>
public sealed record JsonArrayColumnHeaderSegment(string Text, string? Key);

/// <summary>A column's header: its route spelled out as clickable pieces, plus the whole path as
/// one string for the tooltip.</summary>
public sealed record JsonArrayColumnHeader(IReadOnlyList<JsonArrayColumnHeaderSegment> Segments, string Display);

/// <summary>What one pass over the sampled elements produced.</summary>
/// <param name="Truncated">The column cap stopped this discovery short, so some columns the data
/// has are not drawn - the only outcome here that is worth telling the user about.</param>
public sealed record DiscoveredColumns(
    CsvStructure Structure,
    ExpandedRoutes Routes,
    IReadOnlyList<ColumnNesting> Nesting,
    IReadOnlyList<JsonArrayColumnHeader> Headers,
    bool SawObject,
    bool Truncated);

/// <summary>
/// Which container columns are open, keyed by the route that reaches them.
///
/// Closing a container closes everything under it: reopening it should show what its own data
/// says, not a stale expansion from three clicks ago.
///
/// The keys are built by <see cref="JsonArrayColumnDiscovery"/> and are opaque here - all this
/// type knows is that a key's descendants start with it, which is exact rather than approximate
/// because of the marker characters those keys are built from (see
/// <see cref="JsonArrayColumnDiscovery.NameMarker"/>).
/// </summary>
public sealed class OpenColumns
{
    private readonly HashSet<string> open = new(StringComparer.Ordinal);

    public int Count => this.open.Count;

    public bool IsOpen(string key) => this.open.Contains(key);

    /// <summary>Opens a closed container or closes an open one, and reports which it did.</summary>
    public bool Toggle(string key)
    {
        if (this.open.Remove(key))
        {
            CloseUnder(key);
            return false;
        }

        this.open.Add(key);
        return true;
    }

    public void Close(string key)
    {
        this.open.Remove(key);
        CloseUnder(key);
    }

    public void CloseAll() => this.open.Clear();

    private void CloseUnder(string key)
        => this.open.RemoveWhere(candidate => candidate.Length > key.Length && candidate.StartsWith(key, StringComparison.Ordinal));
}

/// <summary>
/// Turns a sample of an array's elements into the grid's shape: the columns, the
/// <see cref="ExpandedRoutes"/> that say where each of them lives inside an element, the headers
/// that let the user open and close containers, and what the sample saw nested in each column.
///
/// The walk is the one the table has always done - the sampled elements' direct children, each
/// nested container skipped whole - with one addition: a container whose key is in
/// <see cref="OpenColumns"/> is walked into instead of drawn, and its children become columns. So
/// the cost of discovery tracks what has been opened, and an untouched table costs exactly what
/// it did before.
///
/// <b>Arrays expand to a fixed number of positions, never to their length.</b> An open array
/// draws its first <c>arrayColumns</c> positions and, if it had more, one remainder column
/// showing the array's own summary. That is what keeps
/// <c>$.features[7].geometry.coordinates[7982]</c> from becoming eight thousand columns, and it
/// keeps the real count on screen rather than quietly dropping the tail.
/// </summary>
public static class JsonArrayColumnDiscovery
{
    /// <summary>
    /// Most columns any expansion may produce. A grid past this is unreadable, and every width
    /// and header is per-column work; discovery stops registering here and reports
    /// <see cref="DiscoveredColumns.Truncated"/> rather than building it.
    /// </summary>
    public const int MaxColumns = 150;

    /// <summary>Positions an open array draws when nobody has chosen otherwise.</summary>
    public const int DefaultArrayColumns = 4;

    /// <summary>Most positions an open array may be asked to draw.</summary>
    public const int MaxArrayColumns = 8;

    /// <summary>
    /// Marks a property-name step in a column key. A control character, which JSON REQUIRES to be
    /// escaped inside a string - so it cannot occur in the raw property-name bytes a key is built
    /// from, and a key splits back into steps unambiguously.
    /// </summary>
    internal const char NameMarker = '\u001F';

    /// <summary>Marks an array-position step in a column key. Same argument as
    /// <see cref="NameMarker"/>.</summary>
    internal const char IndexMarker = '\u001E';

    private static readonly char[] Markers = [NameMarker, IndexMarker];

    /// <summary>
    /// Walks <paramref name="sample"/> elements of <paramref name="elements"/> and builds the
    /// whole shape. <paramref name="cellText"/> is the same builder the cells are rendered with,
    /// so a container column is widthed from the summary it will actually show rather than from
    /// its one-byte brace token.
    /// </summary>
    internal static DiscoveredColumns FromSample(JsonStructureIndex index, IByteSource file,
        JsonArrayElementIndex elements, int sample, JsonRowFactory cellText, OpenColumns open, int arrayColumns)
    {
        var walk = new Walk(index, file, cellText, open, Math.Clamp(arrayColumns, 1, MaxArrayColumns));

        for (int e = 0; e < sample; e++)
        {
            int token = elements.TokenForElement(e);
            var element = index.GetToken(token);

            if (element.Kind == JsonTokenKind.StartObject)
                walk.Object(token, element, keyPrefix: string.Empty, displayPrefix: string.Empty, ancestors: []);
            else
                walk.Element(token, element);
        }

        return walk.Finish();
    }

    /// <summary>
    /// The per-discovery state. A class rather than a pile of ref parameters because the walk is
    /// recursive and every level writes into the same column registry.
    /// </summary>
    private sealed class Walk
    {
        private readonly JsonStructureIndex index;
        private readonly IByteSource file;
        private readonly JsonRowFactory cellText;
        private readonly OpenColumns open;
        private readonly int arrayColumns;

        private readonly Dictionary<string, int> columns = new(StringComparer.Ordinal);

        // Which keys sit directly inside which, in the order they were first seen there - the
        // one thing global registration order cannot tell you, and what Finish orders the
        // columns by. Holds the expanded containers too, which are not columns themselves but
        // are where their children belong.
        private readonly Dictionary<string, List<string>> childKeys = new(StringComparer.Ordinal);
        private readonly List<string> displays = [];
        private readonly List<int> maxChars = [];
        private readonly List<ColumnNesting> nesting = [];
        private readonly List<ColumnRoute> routes = [];
        private readonly List<JsonArrayColumnHeader> headers = [];

        private bool sawObject;
        private bool truncated;
        private int valueChars;

        public Walk(JsonStructureIndex index, IByteSource file, JsonRowFactory cellText, OpenColumns open, int arrayColumns)
        {
            this.index = index;
            this.file = file;
            this.cellText = cellText;
            this.open = open;
            this.arrayColumns = arrayColumns;
        }

        /// <summary>A sampled element that is not an object: it has no properties to distribute,
        /// so it only ever widths the single "value" column.</summary>
        public void Element(int token, JsonTokenInfo element)
            => this.valueChars = Math.Max(this.valueChars, RenderedLength(token, element));

        /// <summary>An object's direct children, each one either drawn as a column or - when it
        /// is an open container - walked into.</summary>
        public void Object(int token, JsonTokenInfo container, string keyPrefix, string displayPrefix,
            IReadOnlyList<JsonArrayColumnHeaderSegment> ancestors)
        {
            this.sawObject = true;

            for (int child = token + 1; child < container.EndIndex;)
            {
                var info = this.index.GetToken(child);
                if (info.NameLength >= 0)
                {
                    string name = DisplayText.Read(this.file, info.NameOffset, info.NameLength, out _);
                    Draw(child, info, parent: keyPrefix,
                        key: keyPrefix + NameMarker + name,
                        display: displayPrefix.Length == 0 ? name : displayPrefix + "." + name,
                        segment: displayPrefix.Length == 0 ? name : "." + name,
                        ancestors);
                }

                child = IsContainer(info.Kind) ? info.EndIndex + 1 : child + 1;
            }
        }

        /// <summary>
        /// An open array's first positions, plus a remainder column when it had more. The
        /// remainder's route is the ARRAY's own, so its cell shows the same summary the collapsed
        /// column did - the reader sees the positions drawn AND how many there really are.
        /// </summary>
        private void Array(int token, JsonTokenInfo container, string key, string display,
            IReadOnlyList<JsonArrayColumnHeaderSegment> ancestors)
        {
            int position = 0;
            int child = token + 1;
            for (; child < container.EndIndex && position < this.arrayColumns; position++)
            {
                var info = this.index.GetToken(child);
                Draw(child, info, parent: key,
                    key: key + IndexMarker + position,
                    display: display + "[" + position + "]",
                    segment: "[" + position + "]",
                    ancestors);

                child = IsContainer(info.Kind) ? info.EndIndex + 1 : child + 1;
            }

            if (child >= container.EndIndex)
                return;

            // More positions than were drawn. Registered against the ARRAY - same route, so the
            // cell is the summary the collapsed column showed - under a key of its own, which is
            // never parsed back into steps because they are handed over here.
            int column = Register(key, key + NameMarker, display + "[…]", "[…]", ancestors, StepsFromKey(key));
            if (column < 0)
                return;

            this.maxChars[column] = Math.Max(this.maxChars[column], RenderedLength(token, container));
            this.nesting[column] = Nested(this.nesting[column], token, container);
        }

        /// <summary>One child: walked into when it is an open container, drawn as a column
        /// otherwise.</summary>
        private void Draw(int token, JsonTokenInfo info, string parent, string key, string display, string segment,
            IReadOnlyList<JsonArrayColumnHeaderSegment> ancestors)
        {
            bool isContainer = IsContainer(info.Kind) && info.EndIndex >= 0;
            if (isContainer && this.open.IsOpen(key))
            {
                // An open container draws no column of its own, but it still holds a place among
                // its siblings - that place is where all of its children are drawn.
                Place(parent, key);

                var inside = Append(ancestors, new JsonArrayColumnHeaderSegment(segment, key));
                if (info.Kind == JsonTokenKind.StartObject)
                    Object(token, info, key, display, inside);
                else
                    Array(token, info, key, display, inside);

                return;
            }

            // Only a container offers to open: the last piece of a scalar column's header is
            // plain text, which is what tells the two apart on screen.
            int column = Register(parent, key, display, segment, ancestors, steps: null, opensTo: isContainer ? key : null);
            if (column < 0)
                return;

            this.maxChars[column] = Math.Max(this.maxChars[column], RenderedLength(token, info));
            this.nesting[column] = Nested(this.nesting[column], token, info);
        }

        private int Register(string parent, string key, string display, string segment,
            IReadOnlyList<JsonArrayColumnHeaderSegment> ancestors, RouteStep[]? steps, string? opensTo = null)
        {
            if (this.columns.TryGetValue(key, out int existing))
                return existing;

            if (this.columns.Count >= MaxColumns)
            {
                this.truncated = true;
                return -1;
            }

            int column = this.displays.Count;
            this.columns[key] = column;
            Place(parent, key);
            this.displays.Add(display);
            this.maxChars.Add(display.Length);
            this.nesting.Add(ColumnNesting.Scalar);
            this.routes.Add(new ColumnRoute(steps ?? StepsFromKey(key), display));
            this.headers.Add(new JsonArrayColumnHeader(
                Append(ancestors, new JsonArrayColumnHeaderSegment(segment, opensTo)), display));

            return column;
        }

        /// <summary>Records where a key sits among the things directly inside
        /// <paramref name="parent"/>, first-seen order, once.</summary>
        private void Place(string parent, string key)
        {
            if (!this.childKeys.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                this.childKeys[parent] = siblings;
            }

            if (!siblings.Contains(key))
                siblings.Add(key);
        }

        /// <summary>
        /// The order the columns are drawn in: depth-first through the keys, siblings in the
        /// order they were first seen inside their own container.
        ///
        /// Registration order alone gets this wrong, and visibly so. A property that first turns
        /// up in element 22 - GeoJSON's <c>geometry.geometries</c>, which only a
        /// GeometryCollection has - is registered after everything the earlier elements held, so
        /// it lands past <c>properties</c> instead of beside the other <c>geometry.*</c> columns.
        /// An expanded container's columns have to read as a group whatever element each one was
        /// discovered from.
        ///
        /// Costs a walk over the registered columns - at most <see cref="MaxColumns"/> of them -
        /// once per discovery. No token is re-read and the sample is not walked again.
        /// </summary>
        private List<int> DrawingOrder()
        {
            var order = new List<int>(this.displays.Count);
            Visit(string.Empty);
            return order;

            void Visit(string parent)
            {
                if (!this.childKeys.TryGetValue(parent, out var siblings))
                    return;

                foreach (string key in siblings)
                {
                    // A column and a container are not exclusive: an open array draws its
                    // positions AND a remainder column keyed on the array itself.
                    if (this.columns.TryGetValue(key, out int column))
                        order.Add(column);

                    Visit(key);
                }
            }
        }

        /// <summary>Puts the parallel per-column lists into <see cref="DrawingOrder"/>. Routes
        /// carry no column index of their own - <see cref="ExpandedRoutes.Build"/> takes it from
        /// the position in the list - so re-ordering the list IS the remap.</summary>
        private void Reorder()
        {
            var order = DrawingOrder();
            if (order.Count != this.displays.Count)
                return; // a column registered under no parent would be dropped; leave the order alone

            Permute(this.displays, order);
            Permute(this.maxChars, order);
            Permute(this.nesting, order);
            Permute(this.routes, order);
            Permute(this.headers, order);
        }

        private static void Permute<T>(List<T> values, List<int> order)
        {
            var reordered = new T[order.Count];
            for (int i = 0; i < order.Count; i++)
                reordered[i] = values[order[i]];

            values.Clear();
            values.AddRange(reordered);
        }

        public DiscoveredColumns Finish()
        {
            if (!this.sawObject)
            {
                // An array of scalars: one column that IS the element, so there is no route into
                // an element to take and no header piece to click.
                this.displays.Add("value");
                this.maxChars.Add(Math.Max(this.valueChars, "value".Length));
                this.nesting.Add(ColumnNesting.Scalar);
                this.headers.Add(new JsonArrayColumnHeader([new JsonArrayColumnHeaderSegment("value", null)], "value"));

                return new DiscoveredColumns(Structure(), ExpandedRoutes.None, this.nesting, this.headers,
                    SawObject: false, this.truncated);
            }

            Reorder();

            return new DiscoveredColumns(Structure(), ExpandedRoutes.Build(this.routes), this.nesting, this.headers,
                SawObject: true, this.truncated);
        }

        private CsvStructure Structure()
            => CsvStructure.FromMaxChars(this.displays,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(this.maxChars));

        /// <summary>
        /// Characters the cell for this token will actually render. A scalar's raw token length
        /// is that already (quotes included, which the cell shows), but a container's is the
        /// brace alone - one character - while the cell shows a summary like <c>{ 6 members }</c>.
        /// Widthing a column of nested objects from the brace is what left every container column
        /// at the minimum width, trimmed to "{ 6 mem...", so containers are measured from the
        /// summary itself. It is built here, for a bounded sample, and thrown away.
        /// </summary>
        private int RenderedLength(int tokenIndex, JsonTokenInfo token)
            => IsContainer(token.Kind)
                ? this.cellText.BuildContainerSummary(tokenIndex, token, expanded: false).Length
                : token.Length;

        /// <summary>Folds one sampled value into what its column has been seen to hold. Scalars
        /// leave it alone; a container contributes its byte span, and an array its arity.</summary>
        private ColumnNesting Nested(ColumnNesting seen, int tokenIndex, JsonTokenInfo token)
        {
            if (!IsContainer(token.Kind) || token.EndIndex < 0)
                return seen;

            var end = this.index.GetToken(token.EndIndex);
            long bytes = end.Offset + end.Length - token.Offset;

            return token.Kind == JsonTokenKind.StartObject
                ? seen.WithObject(bytes)
                : seen.WithArray(CountChildren(tokenIndex, token, ColumnNesting.ArityCap), bytes);
        }

        /// <summary>
        /// Direct children of a container, counting no further than <paramref name="cap"/> - the
        /// same subtree-skipping hop the row walk uses, stopped early because the answer is only
        /// ever compared against how many columns an expansion would draw. O(cap) per sampled
        /// container, no file read.
        /// </summary>
        private int CountChildren(int containerTokenIndex, JsonTokenInfo container, int cap)
        {
            int count = 0;
            for (int child = containerTokenIndex + 1; child < container.EndIndex && count < cap;)
            {
                var info = this.index.GetToken(child);
                count++;
                child = IsContainer(info.Kind) ? info.EndIndex + 1 : child + 1;
            }

            return count;
        }
    }

    private static JsonArrayColumnHeaderSegment[] Append(
        IReadOnlyList<JsonArrayColumnHeaderSegment> segments, JsonArrayColumnHeaderSegment last)
    {
        var appended = new JsonArrayColumnHeaderSegment[segments.Count + 1];
        for (int i = 0; i < segments.Count; i++)
            appended[i] = segments[i];

        appended[^1] = last;
        return appended;
    }

    /// <summary>
    /// Splits a column key back into the steps that reach its value. Unambiguous because neither
    /// marker can appear in a raw property name (see <see cref="NameMarker"/>), so this is a
    /// parse rather than a guess.
    /// </summary>
    private static RouteStep[] StepsFromKey(string key)
    {
        var steps = new List<RouteStep>();
        int at = 0;
        while (at < key.Length)
        {
            char marker = key[at];
            int next = key.IndexOfAny(Markers, at + 1);
            string part = next < 0 ? key[(at + 1)..] : key[(at + 1)..next];

            steps.Add(marker == IndexMarker ? RouteStep.At(int.Parse(part)) : RouteStep.Property(part));
            at = next < 0 ? key.Length : next;
        }

        return steps.ToArray();
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;
}
