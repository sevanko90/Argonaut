namespace Argonaut.Ui.Tree;

/// <summary>
/// What a stretch of row text is, for colouring. Named for roles every tree format has rather
/// than for any one format's syntax, so one palette serves them all: a JSON key and an XML
/// element name are both a <see cref="Name"/>.
/// </summary>
public enum TreeRunStyle : byte
{
    Plain,
    Name,
    AttributeName,
    AttributeValue,
    String,
    Number,
    Keyword,
    Punctuation,
    Summary,
    Comment,

    /// <summary>A note after the value - a decoded date, a truncation notice - that is not part
    /// of the document.</summary>
    Hint,

    /// <summary>Text that acts when clicked; see <see cref="TreeRun.Link"/>.</summary>
    Link,
}

/// <summary>One stretch of a row's text in one style.</summary>
/// <param name="Link">When set, clicking this run raises <c>TreeSurface.LinkClicked</c> with it -
/// the format's own token for what the link does. The surface only knows it is clickable.</param>
public readonly record struct TreeRun(string Text, TreeRunStyle Style, object? Link = null);
