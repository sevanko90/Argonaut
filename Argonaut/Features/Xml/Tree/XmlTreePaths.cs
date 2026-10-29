using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Paths;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// XPaths for the XML tree, in <see cref="XmlPathSyntax"/>'s grammar and with the document's own
/// names: built from a row and its ancestors, which the cursor already holds, and resolved by
/// reading down from the top one container at a time.
///
/// A step is <c>name[k]</c>: the k-th sibling with that name, not the k-th child, so <c>k</c> is
/// counted among siblings of the same key (an element's name as written; text and CDATA together;
/// comments; processing instructions by target). In a recorded container the count comes from the
/// sibling counts stored beside the checkpoints (<see cref="XmlSiblingCounts"/>) plus a read from
/// the nearest checkpoint, so a step costs at most a checkpoint stride of reading however far into
/// the container the row is; an unrecorded container is small enough to count by reading. A
/// container with too many distinct names keeps no counts and is counted by reading.
///
/// <c>[1]</c> is left out when a node is the only sibling of its key: known for an unrecorded
/// container by reading it, for a recorded one once it has closed and its totals are stored. While
/// a recorded container is still being indexed, every step in it is numbered. At the document level
/// an element is unique unless a second root can be read, since a document has one; any other
/// top-level node is numbered until the index is complete.
/// </summary>
public static class XmlTreePaths
{
    private const int MaxNameBytes = 4096;

    private enum KeyKind : byte
    {
        Element,
        AnyElement,
        Text,
        Comment,
        ProcessingInstruction,
        AnyProcessingInstruction,
    }

    /// <summary>What siblings are counted by: a kind, and for a named one the name's bytes and the
    /// hash the index stores counts under.</summary>
    private readonly struct Key(KeyKind kind, ulong hash, byte[]? name, int nameLength)
    {
        public KeyKind Kind { get; } = kind;
        public ulong Hash { get; } = hash;
        public ReadOnlySpan<byte> Name => name.AsSpan(0, nameLength);

        /// <summary>Whether the index keeps counts for it: not a wildcard.</summary>
        public bool IsCounted => Kind is not (KeyKind.AnyElement or KeyKind.AnyProcessingInstruction);
    }

    /// <summary>The path to the cursor's row, a segment per level. A close row's path is its
    /// element's. The XML declaration and a DOCTYPE are not XPath nodes: their path is the single
    /// segment <c>/</c>.</summary>
    public static IReadOnlyList<TreePathSegment> Segments(TreeCursor cursor, XmlTreeText text, XmlSparseIndex index)
    {
        var chain = cursor.Ancestors.ToList();
        var current = cursor.Current;
        chain.Add(current.Shape == TreeRowShape.Close ? current with { Shape = TreeRowShape.Open, Start = current.Node.RowStart } : current);

        if (current.Node.FormatKind is (byte)XmlNodeKind.XmlDeclaration or (byte)XmlNodeKind.Doctype)
            return [new TreePathSegment("/", current.Start)];

        using var walk = new Walk(text, index);
        var segments = new List<TreePathSegment>(chain.Count);
        for (int i = 0; i < chain.Count; i++)
            segments.Add(new TreePathSegment("/" + walk.StepOf(i == 0 ? null : chain[i - 1].Node, chain[i].Node), chain[i].Start));

        return segments;
    }

    public static string Format(IReadOnlyList<TreePathSegment> segments)
    {
        var path = new StringBuilder();
        foreach (var segment in segments)
            path.Append(segment.Label);
        return path.ToString();
    }

    public static TreePathResult Resolve(XmlSparseIndex index, XmlTreeReader reader, XmlTreeText text, string path)
    {
        if (!XmlPathSyntax.TryParse(path, out var steps, out string? parseError))
            return new TreePathResult(null, parseError);

        using var walk = new Walk(text, index);
        TreeNode? current = null;
        if (steps.Count == 0)
        {
            long first = 0;
            return reader.TryReadChild(reader.DocumentKind, ref first, out var top, out _)
                ? new TreePathResult(top.RowStart, null)
                : new TreePathResult(null, "File is empty.");
        }

        for (int s = 0; s < steps.Count; s++)
        {
            var found = current is { IsContainer: false }
                ? null
                : walk.Find(current, steps[s]);
            if (found is not { } next)
                return new TreePathResult(null, $"No {XmlPathSyntax.FormatStep(steps[s])} found under {XmlPathSyntax.FormatPath(steps, s)}.");

            current = next;
        }

        return new TreePathResult(current!.Value.RowStart, null);
    }

    /// <summary>The reads a path needs, over one source and index, with the scratch space they
    /// share rented once.</summary>
    private sealed class Walk : IDisposable
    {
        private readonly IByteSource source;
        private readonly SparseContainerIndex structure;
        private readonly XmlSiblingCounts siblingCounts;
        private readonly XmlTreeReader reader;
        private byte[]? nameBuffer = ArrayPool<byte>.Shared.Rent(MaxNameBytes);
        private byte[]? probeBuffer = ArrayPool<byte>.Shared.Rent(MaxNameBytes + 1);

        public Walk(XmlTreeText text, XmlSparseIndex index)
        {
            source = text.Bytes;
            structure = index.Structure;
            siblingCounts = index.SiblingCounts;
            reader = new XmlTreeReader(source);
        }

        public void Dispose()
        {
            if (nameBuffer is not null)
                ArrayPool<byte>.Shared.Return(nameBuffer);
            if (probeBuffer is not null)
                ArrayPool<byte>.Shared.Return(probeBuffer);
            nameBuffer = probeBuffer = null;
        }

        // ---- spelling a row -------------------------------------------------------------------

        /// <summary>The step to <paramref name="node"/> inside <paramref name="parent"/> (null for
        /// the document level), without its leading slash.</summary>
        public string StepOf(TreeNode? parent, TreeNode node)
        {
            var key = KeyOfNode(node, out string spelled);
            var (k, unique) = parent is { } container ? NumberInElement(container, node, key) : NumberAtDocument(node, key);
            return unique ? spelled : $"{spelled}[{k}]";
        }

        private Key KeyOfNode(TreeNode node, out string spelled)
        {
            switch ((XmlNodeKind)node.FormatKind)
            {
                case XmlNodeKind.Text or XmlNodeKind.CData:
                    spelled = "text()";
                    return new Key(KeyKind.Text, XmlSiblingCounts.TextKey, null, 0);
                case XmlNodeKind.Comment or XmlNodeKind.Doctype:
                    spelled = "comment()";
                    return new Key(KeyKind.Comment, XmlSiblingCounts.CommentKey, null, 0);
                case XmlNodeKind.ProcessingInstruction:
                {
                    int length = ReadName(node.ValueStart + 2);
                    spelled = $"processing-instruction('{Encoding.UTF8.GetString(nameBuffer!, 0, length)}')";
                    return new Key(KeyKind.ProcessingInstruction, XmlSiblingCounts.ProcessingInstructionKeyOf(nameBuffer.AsSpan(0, length)), nameBuffer, length);
                }
                default:
                {
                    int length = ReadName(node.ValueStart + 1);
                    spelled = Encoding.UTF8.GetString(nameBuffer!, 0, length);
                    return new Key(KeyKind.Element, XmlSiblingCounts.KeyOf(nameBuffer.AsSpan(0, length)), nameBuffer, length);
                }
            }
        }

        /// <summary>Reads the name at <paramref name="at"/> into the name buffer; its length.</summary>
        private int ReadName(long at)
        {
            int copied = source.CopyTo(at, nameBuffer.AsSpan(0, MaxNameBytes));
            int end = nameBuffer.AsSpan(0, copied).IndexOfAny(XmlStructuralScanner.NameEnd);
            return end < 0 ? copied : end;
        }

        // ---- k: counting siblings -------------------------------------------------------------

        /// <summary>A top-level node's k, and whether it is the only one of its key.</summary>
        private (long K, bool Unique) NumberAtDocument(TreeNode node, in Key key)
        {
            long k = CountMatches(reader.DocumentKind, 0, node.ValueStart, long.MaxValue, key, out _) + 1;
            if (k != 1 || !(key.Kind == KeyKind.Element || structure.IsComplete))
                return (k, false);

            // After an element only a second root, which a document may not have, would make it
            // one of several, so it need not wait for the index to complete. Anything else could
            // still be followed by a sibling that has not been scanned.
            // An element still open has nothing after it yet.
            long end = EndOf(node);
            return (k, end == long.MaxValue || CountMatches(reader.DocumentKind, end, long.MaxValue, long.MaxValue, key, out _) == 0);
        }

        private (long K, bool Unique) NumberInElement(TreeNode parent, TreeNode node, in Key key)
        {
            const byte ElementKind = (byte)XmlNodeKind.Element;
            int record = structure.FindContainerStartingAt(parent.ValueStart);
            long first = reader.FirstChildPosition(parent.ValueStart);

            if (record < 0)
            {
                // Not recorded: under the promotion size once the scan has passed it, so counted
                // by reading. The rest of the container is read only as far as that size, which is
                // how a container the scan has not reached yet is told from a small one.
                long k = CountMatches(ElementKind, first, node.ValueStart, long.MaxValue, key, out _) + 1;
                if (k != 1)
                    return (k, false);

                long end = EndOf(node);
                if (end == long.MaxValue)
                    return (k, false);

                long after = CountMatches(ElementKind, end, long.MaxValue, parent.ValueStart + structure.PromotionBytes, key, out bool closed);
                return (k, closed && after == 0);
            }

            var container = structure.GetContainer(record);
            long from = first, before = 0;
            int checkpoint = OwnCheckpointAtOrBefore(record, container.Start, node.ValueStart);
            if (checkpoint >= 0 && siblingCounts.TryGetBefore(checkpoint, key.Hash, out long stored))
                (from, before) = (structure.GetCheckpoint(checkpoint).Offset, stored);

            long number = before + CountMatches(ElementKind, from, node.ValueStart, long.MaxValue, key, out _) + 1;
            if (number != 1 || container.IsOpen)
                return (number, false);

            if (siblingCounts.TryGetTotal(record, key.Hash, out long total))
                return (number, total == 1);

            // A closed container that kept no counts: the rest of it is read.
            long nodeEnd = EndOf(node);
            return (number, nodeEnd != long.MaxValue
                && CountMatches(ElementKind, nodeEnd, long.MaxValue, long.MaxValue, key, out bool read) == 0 && read);
        }

        /// <summary>
        /// The last checkpoint <i>of</i> <paramref name="container"/> - not of something inside it -
        /// at or before <paramref name="offset"/>, or -1. A checkpoint found inside a child is
        /// passed over by jumping to where that child starts, so a huge child costs one step, not
        /// one per checkpoint in it.
        /// </summary>
        private int OwnCheckpointAtOrBefore(int container, long containerStart, long offset)
        {
            int c = LastCheckpointAtOrBefore(offset);
            while (c >= 0)
            {
                var checkpoint = structure.GetCheckpoint(c);
                if (checkpoint.Offset <= containerStart)
                    return -1;
                if (checkpoint.Container == container)
                    return c;

                int child = checkpoint.Container;
                while (true)
                {
                    var inside = structure.GetContainer(child);
                    if (inside.Parent == container)
                        break;
                    if (inside.Parent < 0)
                        return -1; // not inside this container at all

                    child = inside.Parent;
                }

                // A checkpoint at the child's own start is the container's, and sorts before the
                // child's.
                c = LastCheckpointAtOrBefore(structure.GetContainer(child).Start);
            }

            return -1;
        }

        private int LastCheckpointAtOrBefore(long offset)
        {
            int low = 0, high = structure.CheckpointCount - 1, found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (structure.GetCheckpoint(middle).Offset <= offset)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }

        // ---- reading siblings -----------------------------------------------------------------

        /// <summary>How many children of the container read from <paramref name="from"/> match
        /// <paramref name="key"/>, among those that start before <paramref name="stopAt"/>.
        /// <paramref name="closed"/> is whether the reading ended at the container's own close tag
        /// rather than at <paramref name="stopAt"/>, the end of the data, or
        /// <paramref name="limit"/> (a position past which it gives up).</summary>
        private long CountMatches(byte parentKind, long from, long stopAt, long limit, in Key key, out bool closed)
        {
            closed = false;
            long count = 0;
            long position = from;
            bool inDocument = parentKind == reader.DocumentKind;
            while (true)
            {
                if (!reader.TryReadChild(parentKind, ref position, out var child, out long closeStart))
                {
                    closed = !inDocument && IsCloseTag(closeStart);
                    return count;
                }

                if (child.ValueStart >= stopAt)
                    return count;

                if (Matches(child, key, inDocument))
                    count++;

                position = EndOf(child);
                if (position == long.MaxValue || position > limit)
                    return count;
            }
        }

        private bool IsCloseTag(long at)
            => at + 1 < source.AvailableLength && source.ByteAt(at) == (byte)'<' && source.ByteAt(at + 1) == (byte)'/';

        /// <summary>Where a child ends. A container the index has recorded says so itself, and
        /// <c>long.MaxValue</c> while it is open - never a scan through what may be gigabytes.</summary>
        private long EndOf(TreeNode child)
        {
            if (!child.IsContainer)
                return child.ValueEnd;

            int record = structure.FindContainerStartingAt(child.ValueStart);
            if (record < 0)
                return reader.SkipValue(child.ValueStart);

            long end = structure.GetContainer(record).End;
            return end >= 0 ? end : long.MaxValue;
        }

        private bool Matches(TreeNode child, in Key key, bool inDocument)
        {
            var kind = (XmlNodeKind)child.FormatKind;
            switch (key.Kind)
            {
                case KeyKind.AnyElement:
                    return IsElement(kind);
                case KeyKind.Element:
                    return IsElement(kind) && NameIs(child.ValueStart + 1, key.Name);
                case KeyKind.Text:
                    return kind is XmlNodeKind.Text or XmlNodeKind.CData;
                case KeyKind.Comment:
                    // The scan counts a DOCTYPE inside an element with the comments.
                    return kind == XmlNodeKind.Comment || (kind == XmlNodeKind.Doctype && !inDocument);
                case KeyKind.AnyProcessingInstruction:
                    return kind == XmlNodeKind.ProcessingInstruction;
                default:
                    return kind == XmlNodeKind.ProcessingInstruction && NameIs(child.ValueStart + 2, key.Name);
            }
        }

        private static bool IsElement(XmlNodeKind kind) => kind is XmlNodeKind.Element or XmlNodeKind.TextElement or XmlNodeKind.EmptyElement;

        /// <summary>Whether the name at <paramref name="at"/> is exactly <paramref name="name"/>:
        /// its bytes, then a byte that ends a name.</summary>
        private bool NameIs(long at, ReadOnlySpan<byte> name)
        {
            int copied = source.CopyTo(at, probeBuffer.AsSpan(0, name.Length + 1));
            return copied >= name.Length
                && probeBuffer.AsSpan(0, name.Length).SequenceEqual(name)
                && (copied == name.Length || XmlStructuralScanner.NameEnd.Contains(probeBuffer![name.Length]));
        }

        // ---- finding the k-th ------------------------------------------------------------------

        /// <summary>The node a step reaches inside <paramref name="parent"/> (null: the document
        /// level), or null.</summary>
        public TreeNode? Find(TreeNode? parent, XmlPathSyntax.Step step)
        {
            long need = Math.Max(1, step.Position);
            var key = KeyOfStep(step);
            byte parentKind = parent is null ? reader.DocumentKind : (byte)XmlNodeKind.Element;
            long from = 0, before = 0;
            if (parent is { } container)
            {
                from = reader.FirstChildPosition(container.ValueStart);
                int record = structure.FindContainerStartingAt(container.ValueStart);
                if (record >= 0 && key.IsCounted)
                    (from, before) = StartingPoint(record, key.Hash, need, from);
            }

            return FindNth(parentKind, from, key, need - before);
        }

        private Key KeyOfStep(XmlPathSyntax.Step step)
        {
            switch (step.Kind)
            {
                case XmlPathSyntax.StepKind.AnyElement:
                    return new Key(KeyKind.AnyElement, 0, null, 0);
                case XmlPathSyntax.StepKind.Text:
                    return new Key(KeyKind.Text, XmlSiblingCounts.TextKey, null, 0);
                case XmlPathSyntax.StepKind.Comment:
                    return new Key(KeyKind.Comment, XmlSiblingCounts.CommentKey, null, 0);
                case XmlPathSyntax.StepKind.ProcessingInstruction when step.Name is null:
                    return new Key(KeyKind.AnyProcessingInstruction, 0, null, 0);
                case XmlPathSyntax.StepKind.ProcessingInstruction:
                {
                    byte[] target = Encoding.UTF8.GetBytes(step.Name!);
                    return new Key(KeyKind.ProcessingInstruction, XmlSiblingCounts.ProcessingInstructionKeyOf(target), target, target.Length);
                }
                default:
                {
                    byte[] name = Encoding.UTF8.GetBytes(step.Name!);
                    return new Key(KeyKind.Element, XmlSiblingCounts.KeyOf(name), name, name.Length);
                }
            }
        }

        /// <summary>
        /// Where to start reading for the <paramref name="need"/>-th child with
        /// <paramref name="hash"/>: the latest of the container's own checkpoints with fewer than
        /// that many before it, and how many it has. Found by binary search over the container's
        /// checkpoints, a checkpoint inside a child standing for the container's last one before
        /// it. The container's first child when it kept no counts.
        /// </summary>
        private (long From, long Before) StartingPoint(int record, ulong hash, long need, long firstChild)
        {
            var container = structure.GetContainer(record);
            int low = LastCheckpointAtOrBefore(container.Start) + 1;
            int high = container.IsOpen ? structure.CheckpointCount - 1 : LastCheckpointAtOrBefore(container.End - 1);

            int best = -1;
            long bestBefore = 0;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                int own = structure.GetCheckpoint(middle).Container == record
                    ? middle
                    : OwnCheckpointAtOrBefore(record, container.Start, structure.GetCheckpoint(middle).Offset);

                long stored = 0;
                if (own >= 0 && !siblingCounts.TryGetBefore(own, hash, out stored))
                    return (firstChild, 0);

                if (stored < need)
                {
                    if (own > best)
                        (best, bestBefore) = (own, stored);
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return best < 0 ? (firstChild, 0) : (structure.GetCheckpoint(best).Offset, bestBefore);
        }

        private TreeNode? FindNth(byte parentKind, long from, in Key key, long need)
        {
            bool inDocument = parentKind == reader.DocumentKind;
            long position = from;
            while (reader.TryReadChild(parentKind, ref position, out var child, out _))
            {
                if (Matches(child, key, inDocument) && --need == 0)
                    return child;

                position = EndOf(child);
                if (position == long.MaxValue)
                    return null;
            }

            return null;
        }
    }
}
