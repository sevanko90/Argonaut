using System.Text;
using System.Text.Json;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Tests.Support;

/// <summary>
/// A JSON document as <c>Utf8JsonReader</c> reads it - comments skipped, trailing commas allowed,
/// as the tree reads - held whole, for tests to check the sparse tree against something that shares
/// none of its code. Every value is a node, in document order; a member's row starts at its name.
/// </summary>
internal sealed class JsonModel
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="RowStart">The name's opening quote for a member, else the value start.</param>
    /// <param name="ValueStart">The value's first byte.</param>
    /// <param name="End">One past the value's last byte.</param>
    /// <param name="Ordinal">Position among the parent's children.</param>
    /// <param name="Parent">Index of the enclosing node, or -1 at the top.</param>
    /// <param name="RawName">A member's name as written, escapes kept, between its quotes; null
    /// for a value that is not a member.</param>
    /// <param name="Name">A member's name decoded.</param>
    public sealed record Node(
        long RowStart, long ValueStart, long End, int Depth, long Ordinal, JsonTokenType Kind, int Parent,
        string? RawName, string? Name, List<int> Children)
    {
        public bool IsContainer => Kind is JsonTokenType.StartObject or JsonTokenType.StartArray;
    }

    /// <summary>A display row: a node's own row, or an expanded container's closing row.</summary>
    public readonly record struct Row(int Node, bool IsClose);

    private readonly byte[] json;

    public JsonModel(byte[] json)
    {
        this.json = json;
        var open = new Stack<int>();
        var reader = new Utf8JsonReader(json, ReaderOptions);
        long pendingNameStart = -1;
        string? pendingRawName = null, pendingName = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingNameStart = reader.TokenStartIndex;
                    pendingRawName = Encoding.UTF8.GetString(reader.ValueSpan);
                    pendingName = reader.GetString();
                    continue;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    int closing = open.Pop();
                    Nodes[closing] = Nodes[closing] with { End = reader.TokenStartIndex + 1 };
                    continue;
            }

            int parent = open.Count > 0 ? open.Peek() : -1;
            var siblings = parent >= 0 ? Nodes[parent].Children : TopLevel;
            long start = reader.TokenStartIndex;
            var node = new Node(pendingNameStart >= 0 ? pendingNameStart : start, start, reader.BytesConsumed, open.Count,
                siblings.Count, reader.TokenType, parent, pendingRawName, pendingName, []);
            Nodes.Add(node);
            siblings.Add(Nodes.Count - 1);
            pendingNameStart = -1;
            pendingRawName = pendingName = null;
            if (node.IsContainer)
                open.Push(Nodes.Count - 1);
        }
    }

    public List<Node> Nodes { get; } = new();

    /// <summary>The top-level values - one, for a document.</summary>
    public List<int> TopLevel { get; } = new();

    /// <summary>A scalar's bytes as written - a string with its quotes.</summary>
    public string RawValue(int node)
    {
        var n = Nodes[node];
        return Encoding.UTF8.GetString(json, (int)n.ValueStart, (int)(n.End - n.ValueStart));
    }

    /// <summary>The rows the tree shows under <paramref name="expand"/>.</summary>
    public List<Row> Rows(TreeExpandState expand)
    {
        var rows = new List<Row>();
        void Append(int i)
        {
            var node = Nodes[i];
            rows.Add(new Row(i, IsClose: false));
            if (!node.IsContainer || !expand.IsExpanded(node.ValueStart, node.Depth))
                return;

            foreach (int child in node.Children)
                Append(child);
            rows.Add(new Row(i, IsClose: true));
        }

        foreach (int top in TopLevel)
            Append(top);
        return rows;
    }

    /// <summary>The node's JSONPath, in the grammar the tree writes: <c>$</c>, <c>.name</c> or
    /// <c>['name']</c>, <c>[i]</c>.</summary>
    public string Path(int node, Func<string, string> formatMember)
    {
        var segments = new List<string>();
        for (int i = node; Nodes[i].Parent >= 0; i = Nodes[i].Parent)
        {
            var n = Nodes[i];
            var parent = Nodes[n.Parent];
            string label = parent.Kind == JsonTokenType.StartArray ? $"[{n.Ordinal}]" : formatMember(n.Name!);
            segments.Add(label[0] == '[' ? label : "." + label);
        }

        segments.Add("$");
        segments.Reverse();
        return string.Concat(segments);
    }
}
