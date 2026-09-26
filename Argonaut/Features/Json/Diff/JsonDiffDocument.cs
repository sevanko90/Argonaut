using System;
using System.Collections.Generic;
using System.Linq;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Where one node of a diffed document is: its row's start (a member's name) and its value's
/// start - the node's identity. <see cref="Absent"/> on a side the node is not on.
/// </summary>
public readonly record struct JsonDiffNode(long RowStart, long ValueStart)
{
    public static readonly JsonDiffNode Absent = new(-1, -1);

    public bool IsPresent => ValueStart >= 0;

    public static JsonDiffNode Of(TreeNode node) => new(node.RowStart, node.ValueStart);
}

/// <summary>
/// One side of a diff, read through its sparse index: nodes found from where they sit, their
/// children, content hashes, paths and display rows. Everything is read from the bytes on demand,
/// so nothing here grows with the document.
///
/// Not thread-safe: the diff worker and the UI each hold their own over the same session.
/// </summary>
public sealed class JsonDiffDocument
{
    private readonly TreeCursor locator;

    // The last node located, when it was a leaf: find keys matches in document order, and the
    // matches in one row - a name and its value - then cost one seek, not one each.
    private TreeRow[]? lastChain;
    private long lastChainEnd;

    public JsonDiffDocument(IndexedSourceSession<JsonSparseIndex> session)
    {
        Session = session;
        Reader = new JsonTreeReader(session.Bytes);
        Text = new JsonTreeText(session.Bytes, session.Index.Structure, Reader);
        locator = new TreeCursor(session.Index.Structure, Reader, new TreeExpandState(int.MaxValue));
    }

    public IndexedSourceSession<JsonSparseIndex> Session { get; }

    public IByteSource Bytes => Session.Bytes;

    public JsonSparseIndex Index => Session.Index;

    public JsonTreeReader Reader { get; }

    public JsonTreeText Text { get; }

    /// <summary>The document's top-level value, once its first byte has arrived.</summary>
    public TreeNode? Root
    {
        get
        {
            long position = 0;
            return Reader.TryReadChild(JsonTreeReader.Document, ref position, out var root, out _) ? root : null;
        }
    }

    /// <summary>The node at <paramref name="at"/>, read again from its bytes.</summary>
    public TreeNode NodeAt(JsonDiffNode at)
    {
        // Only an object's member has a row start before its value; what else the parent is
        // makes no difference to reading the node.
        byte parentKind = at.RowStart != at.ValueStart ? (byte)JsonTokenKind.StartObject : (byte)JsonTokenKind.StartArray;
        long position = at.RowStart;
        if (!Reader.TryReadChild(parentKind, ref position, out var node, out _) || node.ValueStart != at.ValueStart)
            throw new InvalidOperationException($"No value starts at {at.ValueStart}.");
        return node;
    }

    /// <summary>Where <paramref name="node"/>'s value ends - <c>long.MaxValue</c> for a container
    /// still arriving.</summary>
    public long End(TreeNode node) => Text.End(node);

    public bool HasChildren(TreeNode node) => node.IsContainer && Text.HasChildren(node.ValueStart, node.FormatKind);

    /// <summary>A container's children in order, as far as they have arrived.</summary>
    public IEnumerable<TreeNode> Children(TreeNode container)
    {
        long position = Reader.FirstChildPosition(container.ValueStart);
        while (Reader.TryReadChild(container.FormatKind, ref position, out var child, out _))
        {
            yield return child;
            position = End(child);
            if (position == long.MaxValue)
                yield break;
        }
    }

    /// <summary>The content hash of <paramref name="node"/>'s value; the index must have been
    /// built with hashes, and finished.</summary>
    public ulong Hash(TreeNode node) => Index.ContentHashes!.Hash(node.ValueStart, End(node));

    /// <summary>
    /// The rows from the top-level value down to the deepest node whose bytes hold
    /// <paramref name="offset"/> - a member's name included - each with its ordinal among its
    /// siblings. Empty for an empty document.
    /// </summary>
    public IReadOnlyList<TreeRow> Locate(long offset)
    {
        if (lastChain is not null && offset >= lastChain[^1].Node.RowStart && offset < lastChainEnd)
            return lastChain;

        if (!locator.SeekTo(offset))
            return Array.Empty<TreeRow>();

        var chain = locator.Ancestors.ToList();
        var current = locator.Current;

        // A closing bracket belongs to its container, whose open row is already the last
        // ancestor; the diff draws no close rows.
        if (current.Shape != TreeRowShape.Close)
            chain.Add(current);

        var result = chain.ToArray();
        lastChain = result.Length > 0 && !result[^1].Node.IsContainer ? result : null;
        if (lastChain is not null)
            lastChainEnd = lastChain[^1].Node.ValueEnd;
        return result;
    }

    /// <summary>The JSONPath to the value starting at <paramref name="valueStart"/>.</summary>
    public string Path(long valueStart) => JsonTreePaths.Format(Segments(valueStart));

    /// <summary>The path segments below the container starting at
    /// <paramref name="containerStart"/> down to the value at <paramref name="valueStart"/>, which
    /// lies inside it.</summary>
    public string RelativePath(long valueStart, long containerStart)
    {
        var all = Segments(valueStart);
        int skip = Segments(containerStart).Count;
        return JsonTreePaths.Format(all.Skip(skip).ToList());
    }

    private IReadOnlyList<JsonTreePathSegment> Segments(long valueStart)
    {
        if (!locator.SeekTo(valueStart))
            return Array.Empty<JsonTreePathSegment>();
        return JsonTreePaths.Segments(locator, Text);
    }

    // ── Display rows ──────────────────────────────────────────────────────────────────────

    /// <summary>The pane text for <paramref name="node"/>: its name and its value or summary,
    /// each cut at the display cap.</summary>
    public JsonRow BuildRow(TreeNode node, bool expanded)
    {
        string? name = null;
        if (node.RowStart != node.ValueStart)
        {
            long nameStart = node.RowStart + 1;
            long nameLength = Reader.StringEnd(node.RowStart) - 1 - nameStart;
            name = DisplayText.Read(Bytes, nameStart, (int)Math.Min(int.MaxValue, nameLength), out _);
        }

        var row = new TreeRow(node.IsContainer ? TreeRowShape.Open : TreeRowShape.Leaf, node, node.RowStart, 0, 0,
            JsonTreeReader.Document, -1, expanded);
        string value = node.IsContainer ? Text.Summary(row) : Text.Scalar(row, out _, out _, out _);
        return new JsonRow(node.ValueStart, (JsonTokenKind)node.FormatKind, name, value);
    }
}
