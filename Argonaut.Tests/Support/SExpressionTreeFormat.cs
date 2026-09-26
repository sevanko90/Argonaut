using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Tests.Support;

/// <summary>
/// A second tree format for the generic tree machinery, so it does not drift JSON-shaped while
/// JSON is the only real one. S-expressions: <c>(</c> opens, <c>)</c> closes, atoms are runs of
/// anything else, whitespace separates. It has what JSON lacks and XML will have - no separator
/// bytes, children that are leaves and containers interleaved - and nothing it does not need:
/// no strings, no escapes, no comments.
/// </summary>
internal static class SExpressionTreeFormat
{
    public const byte ListKind = 1;

    /// <summary>One node of the whole-document model the tests compare the index against.</summary>
    public sealed record Node(long Start, long End, int Parent, int Depth, long Ordinal, bool IsList, List<int> Children);

    /// <summary>Feeds <paramref name="builder"/> the structure of the first
    /// <paramref name="stopAt"/> bytes (all of them by default), reporting progress every
    /// <paramref name="advanceStride"/> bytes the way a block scanner would. Completes the index
    /// only when the whole document was scanned.</summary>
    public static void Scan(byte[] document, SparseContainerIndexBuilder builder, int? stopAt = null, int advanceStride = 16)
    {
        int end = stopAt ?? document.Length;
        var hasChild = new Stack<bool>();
        bool topLevelHasValue = false;

        for (int i = 0; i < end; i++)
        {
            if (i % advanceStride == 0)
                builder.Advance(i);

            byte b = document[i];
            if (IsWhitespace(b))
                continue;

            if (b == (byte)')')
            {
                builder.Close(i + 1, isEmpty: !hasChild.Pop());
                continue;
            }

            // A child starts here: separated from its predecessor unless it is the first.
            if (hasChild.Count > 0 ? hasChild.Peek() : topLevelHasValue)
                builder.Separator(i);
            if (hasChild.Count > 0)
            {
                hasChild.Pop();
                hasChild.Push(true);
            }
            else
            {
                topLevelHasValue = true;
            }

            if (b == (byte)'(')
            {
                builder.Open(i, ListKind);
                hasChild.Push(false);
                continue;
            }

            while (i + 1 < end && !IsWhitespace(document[i + 1]) && document[i + 1] is not ((byte)'(' or (byte)')'))
                i++;
        }

        builder.Advance(end);
        if (end == document.Length)
            builder.Complete(end);
    }

    /// <summary>Every node of the document, in start order.</summary>
    public static List<Node> Parse(byte[] document)
    {
        var nodes = new List<Node>();
        var open = new Stack<int>();
        var topLevel = new List<int>();

        for (int i = 0; i < document.Length; i++)
        {
            byte b = document[i];
            if (IsWhitespace(b))
                continue;

            if (b == (byte)')')
            {
                int closing = open.Pop();
                nodes[closing] = nodes[closing] with { End = i + 1 };
                continue;
            }

            int parent = open.Count > 0 ? open.Peek() : -1;
            var siblings = parent >= 0 ? nodes[parent].Children : topLevel;
            int nodeIndex = nodes.Count;

            if (b == (byte)'(')
            {
                nodes.Add(new Node(i, -1, parent, open.Count, siblings.Count, IsList: true, []));
                siblings.Add(nodeIndex);
                open.Push(nodeIndex);
                continue;
            }

            int start = i;
            while (i + 1 < document.Length && !IsWhitespace(document[i + 1]) && document[i + 1] is not ((byte)'(' or (byte)')'))
                i++;
            nodes.Add(new Node(start, i + 1, parent, open.Count, siblings.Count, IsList: false, []));
            siblings.Add(nodeIndex);
        }

        return nodes;
    }

    /// <summary>A random document: a few top-level values, the first a long list so the
    /// document has large containers with many children.</summary>
    public static byte[] Generate(Random random, int topLevelChildren)
    {
        var text = new System.Text.StringBuilder("(");
        for (int i = 0; i < topLevelChildren; i++)
        {
            if (i > 0)
                text.Append(Whitespace(random));
            AppendValue(text, random, depth: 1);
        }

        text.Append(')');
        text.Append(" atom ");
        AppendValue(text, random, depth: 1);
        text.Append(" (");
        for (int i = 0; i < 40; i++)
            text.Append(' ').Append(Atom(random));
        text.Append(')');
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }

    private static void AppendValue(System.Text.StringBuilder text, Random random, int depth)
    {
        // Lists thin out with depth so the document stays a few tens of KB: long lists near
        // the top, short ones down to depth 8.
        if (depth > 8 || random.Next(depth < 3 ? 3 : 5) > 0)
        {
            text.Append(Atom(random));
            return;
        }

        text.Append('(');
        int children = depth <= 2 && random.Next(4) == 0 ? random.Next(40, 120) : random.Next(0, 5);
        for (int i = 0; i < children; i++)
        {
            text.Append(i == 0 ? (random.Next(2) == 0 ? "" : " ") : Whitespace(random));
            AppendValue(text, random, depth + 1);
        }

        text.Append(random.Next(2) == 0 ? ")" : " )");
    }

    private static string Atom(Random random) => new('a', random.Next(1, 30));

    private static string Whitespace(Random random) => random.Next(3) switch
    {
        0 => " ",
        1 => "\n  ",
        _ => new string(' ', random.Next(1, 20)),
    };

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\t' or (byte)'\r';
}
