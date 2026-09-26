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
}

/// <summary>One stretch of a row's text in one style.</summary>
public readonly record struct TreeRun(string Text, TreeRunStyle Style);
