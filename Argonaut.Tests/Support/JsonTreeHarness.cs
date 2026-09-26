using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Support;

/// <summary>
/// A JSON document on the sparse tree as the JSON view reads it - index, reader, text, painter and
/// schema gutter - without a view, so a test can read what each row says. Small promotion and
/// checkpoint sizes, so even a small document goes through recorded containers and resume points.
/// </summary>
internal sealed class JsonTreeHarness
{
    /// <summary>One row as the tree draws it.</summary>
    public sealed record PaintedRow(TreeRow Row, List<TreeRun> Runs, string? Marker)
    {
        /// <summary>The member name, without its ": ", or null.</summary>
        public string? Name => Runs.FirstOrDefault(r => r.Style == TreeRunStyle.Name).Text is { } name ? name[..^2] : null;

        /// <summary>The value's text: a scalar, a bracket or a collapsed summary.</summary>
        public string Value => Runs.First(r => r.Style is not (TreeRunStyle.Name or TreeRunStyle.Hint or TreeRunStyle.Link)).Text;

        /// <summary>The decoded date after the value, or null.</summary>
        public string? DateHint => Runs.FirstOrDefault(r => r.Link is DateSchemeLink).Text?.Trim();

        /// <summary>A plain note after the value (a cut name), or null.</summary>
        public string? Note => Runs.FirstOrDefault(r => r.Style == TreeRunStyle.Hint).Text?.Trim();

        public TreeRun? LinkOf<TLink>() => Runs.Any(r => r.Link is TLink) ? Runs.First(r => r.Link is TLink) : null;
    }

    public JsonTreeHarness(byte[] json, DateHintSettings? hints = null, int promotionBytes = 64, int checkpointBytes = 16)
    {
        Bytes = new MemoryByteSource(json);
        Index = JsonSparseIndex.StartIndexing(Bytes, promotionBytes, checkpointBytes);
        Index.IndexingTask.GetAwaiter().GetResult();
        Reader = new JsonTreeReader(Bytes);
        Text = new JsonTreeText(Bytes, Index.Structure, Reader);
        Painter = new JsonTreePainter(Text, hints is null ? null : new IValueHintProvider[] { new DateHintProvider(hints) }, offerArrayTable: true);
        Resolver = new JsonSchemaResolver(Index.Structure, Reader, Text);
        Gutter = new JsonSchemaGutter(Resolver, Text);
    }

    public JsonTreeHarness(string json, DateHintSettings? hints = null)
        : this(Encoding.UTF8.GetBytes(json), hints)
    {
    }

    public MemoryByteSource Bytes { get; }

    public JsonSparseIndex Index { get; }

    public JsonTreeReader Reader { get; }

    public JsonTreeText Text { get; }

    public JsonTreePainter Painter { get; }

    public JsonSchemaResolver Resolver { get; }

    public JsonSchemaGutter Gutter { get; }

    public JsonSchemaDocument? Schema
    {
        get => Resolver.Schema;
        set => Resolver.Schema = value;
    }

    public TreeCursor Cursor(TreeExpandState expand) => new(Index.Structure, Reader, expand);

    /// <summary>Every row under <paramref name="expand"/>, painted.</summary>
    public List<PaintedRow> Rows(TreeExpandState expand)
    {
        var cursor = Cursor(expand);
        var rows = new List<PaintedRow>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            rows.Add(Paint(cursor.Current));
        return rows;
    }

    /// <summary>Every row with everything expanded.</summary>
    public List<PaintedRow> Rows() => Rows(new TreeExpandState(int.MaxValue));

    /// <summary>The first row with everything expanded whose member name is <paramref name="name"/>.</summary>
    public PaintedRow Member(string name) => Rows().First(r => r.Name == name);

    public PaintedRow Paint(in TreeRow row)
    {
        var runs = new List<TreeRun>();
        Painter.AppendRuns(row, runs);
        return new PaintedRow(row, runs, Painter.Marker(row));
    }
}
