using System.Text;
using System.Xml;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;

namespace Argonaut.Tests.Support;

/// <summary>
/// The XML tree as the plan defines it (<c>docs/xml-view-options.md</c>, "Node kinds"), built by
/// a deliberately simple parser that reads a whole byte array one construct at a time - the
/// slow, obvious answer the scanner, index and reader are compared against.
///
/// Whitespace-only text is not a node. An element whose content holds an element, comment or
/// processing instruction is an <see cref="XmlNodeKind.Element"/> container; one holding only text
/// and CDATA is a <see cref="XmlNodeKind.TextElement"/> leaf; one holding nothing but whitespace
/// is an <see cref="XmlNodeKind.EmptyElement"/>. A text node starts at its first non-whitespace
/// byte and ends at the next <c>&lt;</c>.
///
/// <see cref="CountWithXmlReader"/> cross-checks a document with <c>XmlReader</c>, so a generator
/// or parser mistake shows up as a disagreement rather than as a test of the wrong thing.
/// </summary>
internal static class XmlModel
{
    public sealed class Node
    {
        public long Start { get; set; }
        public long End { get; set; }
        public XmlNodeKind Kind { get; set; }

        /// <summary>The containing node's index in the model, or -1 at the top.</summary>
        public int Parent { get; set; } = -1;
        public int Depth { get; set; }
        public long Ordinal { get; set; }
        public List<int> Children { get; } = [];

        /// <summary>What an XPath step numbers it among: an element's name as written,
        /// <c>#text</c> for text and CDATA, <c>#comment</c>, or <c>#pi:</c> and the target.</summary>
        public string Key { get; set; } = "";

        /// <summary>For an element, one past its start tag - where its content, and a seek into
        /// its children, begins.</summary>
        public long ContentStart { get; set; }

        /// <summary>For an element with a close tag, where that tag's <c>&lt;</c> is.</summary>
        public long CloseStart { get; set; } = -1;

        public override string ToString() => $"{Kind} {Start}-{End} depth {Depth} #{Ordinal} children {Children.Count} {Key}";
    }

    /// <summary>Every node in document order.</summary>
    public static List<Node> Parse(byte[] document)
    {
        var raw = ReadRaw(document);
        foreach (var node in raw.Where(n => n.Kind == XmlNodeKind.Element).Reverse())
            Classify(raw, node);

        var nodes = new List<Node>();
        var topLevel = raw.Where(n => n.Parent < 0).ToList();
        for (int i = 0; i < topLevel.Count; i++)
            Flatten(raw, topLevel[i], parent: -1, depth: 0, ordinal: i, nodes);
        return nodes;
    }

    /// <summary>One display row: a node's own, or an expanded container's close row.</summary>
    public readonly record struct Row(int Node, bool IsClose);

    /// <summary>The rows a tree shows for <paramref name="expand"/>, in order.</summary>
    public static List<Row> Rows(List<Node> nodes, TreeExpandState expand)
    {
        var rows = new List<Row>();
        void Append(int i)
        {
            rows.Add(new Row(i, false));
            var node = nodes[i];
            if (node.Kind != XmlNodeKind.Element || !expand.IsExpanded(node.Start, node.Depth))
                return;
            foreach (int child in node.Children)
                Append(child);
            rows.Add(new Row(i, true));
        }

        foreach (int top in TopLevel(nodes))
            Append(top);
        return rows;
    }

    public static IEnumerable<int> TopLevel(List<Node> nodes) => Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Parent < 0);

    /// <summary>Which of <paramref name="rows"/> shows <paramref name="offset"/>: the deepest
    /// visible node holding it - a collapsed ancestor's row if it is hidden - or, between nodes,
    /// the next one; inside an expanded element's start tag, the element's own row; inside its
    /// close tag, the close row; past the end, the last row.</summary>
    public static int RowShowing(long offset, List<Node> nodes, TreeExpandState expand, List<Row> rows)
    {
        IEnumerable<int> siblings = TopLevel(nodes);
        int? enclosing = null;
        while (true)
        {
            int target = siblings.FirstOrDefault(i => offset < nodes[i].End, -1);
            if (target < 0)
            {
                return enclosing is int container
                    ? rows.FindIndex(r => r.Node == container && r.IsClose)
                    : rows.Count - 1;
            }

            var node = nodes[target];
            if (node.Kind == XmlNodeKind.Element && expand.IsExpanded(node.Start, node.Depth) && offset >= node.ContentStart)
            {
                enclosing = target;
                siblings = node.Children;
                continue;
            }

            return rows.FindIndex(r => r.Node == target && !r.IsClose);
        }
    }

    /// <summary>The model's containers - its <see cref="XmlNodeKind.Element"/> nodes.</summary>
    public static IEnumerable<Node> Containers(List<Node> nodes) => nodes.Where(n => n.Kind == XmlNodeKind.Element);

    /// <summary>How many elements, comments, processing instructions and CDATA sections
    /// <c>XmlReader</c> finds, the XML declaration excluded.</summary>
    public static (int Elements, int Comments, int ProcessingInstructions, int CData) CountWithXmlReader(byte[] document)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(document), settings);
        int elements = 0, comments = 0, instructions = 0, cdata = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element: elements++; break;
                case XmlNodeType.Comment: comments++; break;
                case XmlNodeType.ProcessingInstruction: instructions++; break;
                case XmlNodeType.CDATA: cdata++; break;
            }
        }

        return (elements, comments, instructions, cdata);
    }

    /// <summary>The same four counts from this model's own reading, CDATA inside text-only
    /// elements included.</summary>
    public static (int Elements, int Comments, int ProcessingInstructions, int CData) CountRaw(byte[] document)
    {
        var raw = ReadRaw(document);
        return (raw.Count(n => n.Kind is XmlNodeKind.Element or XmlNodeKind.EmptyElement),
            raw.Count(n => n.Kind == XmlNodeKind.Comment),
            raw.Count(n => n.Kind == XmlNodeKind.ProcessingInstruction),
            raw.Count(n => n.Kind == XmlNodeKind.CData));
    }

    private sealed class RawNode
    {
        public long Start;
        public long End;
        public long ContentStart;
        public long CloseStart = -1;
        public XmlNodeKind Kind;
        public int Parent = -1;
        public int Index;
        public string Key = "";
        public List<int> Children = [];
        public bool Dropped;
    }

    private static List<RawNode> ReadRaw(byte[] doc)
    {
        var raw = new List<RawNode>();
        var open = new Stack<int>();
        int i = 0;

        RawNode Add(long start, long end, XmlNodeKind kind, string key)
        {
            var node = new RawNode { Start = start, End = end, Kind = kind, Key = key, Index = raw.Count, Parent = open.Count > 0 ? open.Peek() : -1 };
            raw.Add(node);
            if (node.Parent >= 0)
                raw[node.Parent].Children.Add(node.Index);
            return node;
        }

        while (i < doc.Length)
        {
            if (doc[i] != (byte)'<')
            {
                int next = IndexOf(doc, i, "<");
                if (next < 0)
                    next = doc.Length;
                int content = i;
                while (content < next && IsWhitespace(doc[content]))
                    content++;
                if (content < next)
                    Add(content, next, XmlNodeKind.Text, "#text");
                i = next;
                continue;
            }

            if (StartsWith(doc, i, "<?"))
            {
                int end = IndexOf(doc, i + 2, "?>") + 2;
                string target = NameAt(doc, i + 2);
                var kind = i == 0 && target == "xml" ? XmlNodeKind.XmlDeclaration : XmlNodeKind.ProcessingInstruction;
                Add(i, end, kind, "#pi:" + target);
                i = end;
            }
            else if (StartsWith(doc, i, "<!--"))
            {
                int end = IndexOf(doc, i + 4, "-->") + 3;
                Add(i, end, XmlNodeKind.Comment, "#comment");
                i = end;
            }
            else if (StartsWith(doc, i, "<![CDATA["))
            {
                int end = IndexOf(doc, i + 9, "]]>") + 3;
                Add(i, end, XmlNodeKind.CData, "#text");
                i = end;
            }
            else if (StartsWith(doc, i, "<!DOCTYPE"))
            {
                int end = DoctypeEnd(doc, i);
                Add(i, end, XmlNodeKind.Doctype, "#doctype");
                i = end;
            }
            else if (StartsWith(doc, i, "</"))
            {
                int end = IndexOf(doc, i, ">") + 1;
                var closing = raw[open.Pop()];
                closing.End = end;
                closing.CloseStart = i;
                i = end;
            }
            else
            {
                int end = TagEnd(doc, i);
                bool selfClosing = doc[end - 2] == (byte)'/';
                var node = Add(i, end, selfClosing ? XmlNodeKind.EmptyElement : XmlNodeKind.Element, NameAt(doc, i + 1));
                node.ContentStart = end;
                if (!selfClosing)
                    open.Push(node.Index);
                i = end;
            }
        }

        if (open.Count > 0)
            throw new InvalidOperationException("The model reads well-formed documents only.");
        return raw;
    }

    /// <summary>Decides an element's kind from its content, children first.</summary>
    private static void Classify(List<RawNode> raw, RawNode element)
    {
        var children = element.Children.Select(c => raw[c]).ToList();
        bool structural = children.Any(c => c.Kind is XmlNodeKind.Element or XmlNodeKind.TextElement or XmlNodeKind.EmptyElement
            or XmlNodeKind.Comment or XmlNodeKind.ProcessingInstruction);
        if (structural)
            return;

        element.Kind = children.Any(c => c.Kind is XmlNodeKind.Text or XmlNodeKind.CData) ? XmlNodeKind.TextElement : XmlNodeKind.EmptyElement;
        foreach (var child in children)
            child.Dropped = true;
        element.Children.Clear();
    }

    private static void Flatten(List<RawNode> raw, RawNode node, int parent, int depth, long ordinal, List<Node> nodes)
    {
        var flat = new Node
        {
            Start = node.Start, End = node.End, Kind = node.Kind, Parent = parent, Depth = depth, Ordinal = ordinal, Key = node.Key,
            ContentStart = node.ContentStart, CloseStart = node.CloseStart,
        };
        int index = nodes.Count;
        nodes.Add(flat);
        if (parent >= 0)
            nodes[parent].Children.Add(index);

        long childOrdinal = 0;
        foreach (int child in node.Children)
            Flatten(raw, raw[child], index, depth + 1, childOrdinal++, nodes);
    }

    private static int TagEnd(byte[] doc, int start)
    {
        byte quote = 0;
        for (int i = start + 1; i < doc.Length; i++)
        {
            byte b = doc[i];
            if (quote != 0)
            {
                if (b == quote)
                    quote = 0;
            }
            else if (b is (byte)'"' or (byte)'\'')
            {
                quote = b;
            }
            else if (b == (byte)'>')
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException("Unterminated tag.");
    }

    /// <summary>A DOCTYPE's end: past the <c>&gt;</c> outside quotes and outside its internal
    /// subset, where comments, processing instructions and quoted literals may hold <c>]</c> and
    /// <c>&gt;</c>.</summary>
    private static int DoctypeEnd(byte[] doc, int start)
    {
        bool inSubset = false;
        for (int i = start + 9; i < doc.Length; i++)
        {
            byte b = doc[i];
            if (b is (byte)'"' or (byte)'\'')
                i = Array.IndexOf(doc, b, i + 1);
            else if (inSubset && StartsWith(doc, i, "<!--"))
                i = IndexOf(doc, i + 4, "-->") + 2;
            else if (inSubset && StartsWith(doc, i, "<?"))
                i = IndexOf(doc, i + 2, "?>") + 1;
            else if (b == (byte)'[')
                inSubset = true;
            else if (b == (byte)']')
                inSubset = false;
            else if (b == (byte)'>' && !inSubset)
                return i + 1;
        }

        throw new InvalidOperationException("Unterminated DOCTYPE.");
    }

    private static string NameAt(byte[] doc, int start)
    {
        int end = start;
        while (end < doc.Length && !IsWhitespace(doc[end]) && doc[end] is not ((byte)'>' or (byte)'/' or (byte)'?'))
            end++;
        return Encoding.UTF8.GetString(doc, start, end - start);
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool StartsWith(byte[] doc, int at, string text)
        => doc.AsSpan(at).StartsWith(Encoding.ASCII.GetBytes(text));

    private static int IndexOf(byte[] doc, int from, string text)
    {
        int found = doc.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(text));
        return found < 0 ? -1 : from + found;
    }
}
