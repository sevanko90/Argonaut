namespace Argonaut.Features.Xml.Indexing;

/// <summary>
/// What a node of the XML tree is - the <c>FormatKind</c> byte of its tree node. Only
/// <see cref="Element"/> is a container: an element whose content holds another element, a
/// comment or a processing instruction. An element holding only text and CDATA is one row
/// (<see cref="TextElement"/>), and one holding nothing but whitespace is another
/// (<see cref="EmptyElement"/>), which is what keeps a tree of XML readable.
/// </summary>
public enum XmlNodeKind : byte
{
    /// <summary>An element with children: its start tag opens a row, its close tag closes one.</summary>
    Element = 1,

    /// <summary>An element whose content is only text and CDATA, shown whole on one row.</summary>
    TextElement = 2,

    /// <summary>A self-closing element, or one whose content is empty or only whitespace.</summary>
    EmptyElement = 3,

    /// <summary>A run of text with a non-whitespace character, beside other children.</summary>
    Text = 4,

    /// <summary>A CDATA section beside other children.</summary>
    CData = 5,

    Comment = 6,

    ProcessingInstruction = 7,

    /// <summary>The <c>&lt;?xml ...?&gt;</c> declaration at the very start of the document.</summary>
    XmlDeclaration = 8,

    /// <summary>A DOCTYPE, its internal subset included.</summary>
    Doctype = 9,

    /// <summary>The document level, holding the top-level nodes.</summary>
    Document = byte.MaxValue,
}
