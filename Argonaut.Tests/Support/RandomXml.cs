using System.Text;

namespace Argonaut.Tests.Support;

/// <summary>
/// Seeded random XML for comparing the XML scanner and reader against <see cref="XmlModel"/>:
/// attributes in both quote styles holding <c>&gt;</c> and the other quote, text with entity and
/// character references and multi-byte characters, mixed content, CDATA holding <c>&lt;</c> and
/// <c>]]</c>, comments and processing instructions holding <c>&lt;</c> and <c>&gt;</c>, a DOCTYPE
/// whose internal subset holds a comment with <c>]</c> in it, namespaces declared on the root and
/// below it, self-closing, empty and whitespace-only elements, and formatting whitespace of every
/// length between children. Every document is well-formed, so <c>XmlReader</c> accepts it.
/// </summary>
internal static class RandomXml
{
    private static readonly string[] Names =
        ["item", "p0:entry", "p1:node", "data", "x_y", "a.b-c", "ñame", "p0:a-much-longer-element-name", "日本"];

    private static readonly string[] Words =
        ["alpha", "bravo", "&amp;", "&lt;tag&gt;", "&#x41;", "&#233;", "é", "日本語", "🚀", "x > y", "\"quoted\"", "it's", "a]b"];

    private static readonly string[] AttributeValues =
        ["1", "a > b", "it's", "say \"hi\"", "&amp;&lt;", "日本", "", "  spaced  "];

    private static readonly string[] CDataContents =
        ["x < y && z", "]] not the end", "<tag attr='1'>", "multi\nline", "", " "];

    private static readonly string[] CommentContents =
        [" note ", " a <b> c ", " x - y ", "\n  multi\n  line\n", " ]]> "];

    private static readonly string[] ProcessingInstructions =
        ["<?app-pi value=\"1 > 0\"?>", "<?p.i?>", "<?render mode='fast' <x>?>"];

    /// <summary>A root element of <paramref name="elements"/> random children, with a random prolog
    /// and trailing comment when <paramref name="prolog"/>.</summary>
    public static byte[] Document(Random random, int elements, int maxDepth = 6, bool prolog = true)
        => Encoding.UTF8.GetBytes(Build(random, elements, maxDepth, prolog, wideChildren: 0));

    /// <summary>As <see cref="Document"/>, but each of the root's children is a group of
    /// <paramref name="wideChildren"/> children or more, so the document has containers well past a
    /// small promotion size at several depths.</summary>
    public static byte[] LargeContainers(Random random, int elements, int wideChildren = 12)
        => Encoding.UTF8.GetBytes(Build(random, elements, maxDepth: 6, prolog: true, wideChildren));

    private static string Build(Random random, int elements, int maxDepth, bool prolog, int wideChildren)
    {
        var text = new StringBuilder();
        if (prolog)
            AppendProlog(text, random);

        text.Append("<p0:root xmlns:p0=\"urn:argonaut:a\" xmlns:p1=\"urn:argonaut:b\" xmlns=\"urn:argonaut:default\" id=\"r\">");
        for (int i = 0; i < elements; i++)
        {
            text.Append(Gap(random, 1));
            if (wideChildren > 0)
                AppendContainer(text, random, "group", depth: 1, maxDepth, wideChildren + random.Next(wideChildren));
            else
                AppendElement(text, random, depth: 1, maxDepth);
        }

        text.Append(Gap(random, 0)).Append("</p0:root>");
        if (prolog && random.Next(2) == 0)
            text.Append("\n<!-- trailing -->\n");
        return text.ToString();
    }

    private static void AppendProlog(StringBuilder text, Random random)
    {
        if (random.Next(2) == 0)
            text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        if (random.Next(2) == 0)
            text.Append("<!-- generated <for> tests -->\n");
        if (random.Next(2) == 0)
        {
            text.Append("<!DOCTYPE p0:root [\n")
                .Append("  <!ELEMENT p0:root ANY>\n")
                .Append("  <!ENTITY copy \"(c)\">\n")
                .Append("  <!-- a ] and a > in the subset -->\n")
                .Append("  <!ATTLIST p0:root id CDATA \"x>y]\">\n")
                .Append("  <?subset-pi ] > ?>\n")
                .Append("]>\n");
        }

        if (random.Next(3) == 0)
            text.Append(ProcessingInstructions[random.Next(ProcessingInstructions.Length)]).Append('\n');
    }

    private static void AppendElement(StringBuilder text, Random random, int depth, int maxDepth)
    {
        string name = Names[random.Next(Names.Length)];
        int shape = random.Next(100);
        if (depth >= maxDepth)
            shape = random.Next(50); // leaves only

        switch (shape)
        {
            case < 10:
                AppendStartTag(text, random, name, selfClosing: true);
                break;
            case < 15:
                AppendStartTag(text, random, name).Append("</").Append(name).Append('>');
                break;
            case < 20:
                AppendStartTag(text, random, name).Append(Gap(random, depth)).Append(Gap(random, depth)).Append("</").Append(name).Append('>');
                break;
            case < 40:
                AppendStartTag(text, random, name).Append(Sentence(random)).Append("</").Append(name).Append('>');
                break;
            case < 45:
                AppendStartTag(text, random, name).Append(Sentence(random)).Append(CData(random)).Append(Sentence(random))
                    .Append("</").Append(name).Append('>');
                break;
            case < 50:
                AppendStartTag(text, random, name).Append(Gap(random, depth)).Append(CData(random)).Append("</").Append(name).Append('>');
                break;
            default:
                AppendContainer(text, random, name, depth, maxDepth, 1 + random.Next(8));
                break;
        }
    }

    private static void AppendContainer(StringBuilder text, Random random, string name, int depth, int maxDepth, int children)
    {
        // A namespace declared below the root, used by the element that declares it.
        if (random.Next(20) == 0)
        {
            name = "p2:local";
            AppendStartTag(text, random, name, declaration: " xmlns:p2=\"urn:argonaut:c\"");
        }
        else
        {
            AppendStartTag(text, random, name);
        }

        for (int i = 0; i < children; i++)
        {
            text.Append(Gap(random, depth + 1));
            switch (random.Next(100))
            {
                case < 70:
                    AppendElement(text, random, depth + 1, maxDepth);
                    break;
                case < 78:
                    text.Append("<!--").Append(CommentContents[random.Next(CommentContents.Length)]).Append("-->");
                    break;
                case < 85:
                    text.Append(ProcessingInstructions[random.Next(ProcessingInstructions.Length)]);
                    break;
                case < 93:
                    text.Append(Sentence(random)); // mixed content
                    break;
                default:
                    text.Append(CData(random));
                    break;
            }
        }

        text.Append(Gap(random, depth)).Append("</").Append(name).Append('>');
    }

    private static StringBuilder AppendStartTag(StringBuilder text, Random random, string name, bool selfClosing = false, string declaration = "")
    {
        text.Append('<').Append(name).Append(declaration);
        int attributes = random.Next(4);
        for (int i = 0; i < attributes; i++)
        {
            string value = AttributeValues[random.Next(AttributeValues.Length)];
            char quote = value.Contains('"') ? '\'' : value.Contains('\'') ? '"' : random.Next(2) == 0 ? '"' : '\'';
            text.Append(random.Next(3) == 0 ? "\n    " : " ").Append("at").Append(i).Append('=').Append(quote).Append(value).Append(quote);
        }

        if (random.Next(4) == 0)
            text.Append(' ');
        return text.Append(selfClosing ? "/>" : ">");
    }

    private static string Sentence(Random random)
    {
        var words = new string[1 + random.Next(5)];
        for (int i = 0; i < words.Length; i++)
            words[i] = Words[random.Next(Words.Length)];
        return string.Join(' ', words);
    }

    private static string CData(Random random) => "<![CDATA[" + CDataContents[random.Next(CDataContents.Length)] + "]]>";

    /// <summary>Formatting whitespace: none, a space, or a newline and indentation - lengths that
    /// slide every construct across a split source's piece boundaries.</summary>
    private static string Gap(Random random, int depth) => random.Next(4) switch
    {
        0 => "",
        1 => " ",
        2 => "\n" + new string(' ', depth * 2),
        _ => "\r\n" + new string('\t', depth) + "  ",
    };
}
