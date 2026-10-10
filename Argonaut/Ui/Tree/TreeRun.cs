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

    /// <summary>A literal that is neither a string, a number nor a keyword - JSON's null.</summary>
    Literal,
    Punctuation,
    Summary,
    Comment,

    /// <summary>A note after the value - a decoded date, a truncation notice - that is not part
    /// of the document.</summary>
    Hint,

    /// <summary>Text that acts when clicked; see <see cref="TreeRun.Link"/>.</summary>
    Link,

    /// <summary>A mark a comparison puts where a difference lies below.</summary>
    Change,
    /// <summary>A remark about the value - a size, a truncation - set small in the interface
    /// font after the row's text. The trailing styles (this, <see cref="Chip"/> and
    /// <see cref="Action"/>) are laid out after every other run, in the order given.</summary>
    Note,
    /// <summary>Information about the value in a chip after the row's text - a decoded date.
    /// Always shown, and clickable when it has a link.</summary>
    Chip,
    /// <summary>Something to do with the value, in a chip after the row's text - open it
    /// elsewhere. Shown only on the hovered or selected row, so a scan down the tree stays calm.</summary>
    Action,
}

/// <summary>The icon a chip leads with, named for what it means rather than how it is drawn; the
/// host maps each to a glyph (<see cref="TreeSurface.RunIcons"/>).</summary>
public enum TreeRunIcon : byte
{
    None,
    /// <summary>A moment in time - a decoded date.</summary>
    Time,
    /// <summary>A table view of the value.</summary>
    Table,
    /// <summary>The value's text in full, elsewhere.</summary>
    FullText,
}

/// <summary>One stretch of a row's text in one style.</summary>
/// <param name="Link">When set, clicking this run raises <c>TreeSurface.LinkClicked</c> with it -
/// the format's own token for what the link does. The surface only knows it is clickable.</param>
/// <param name="Icon">The icon a <see cref="TreeRunStyle.Chip"/> or <see cref="TreeRunStyle.Action"/>
/// leads with; ignored for other styles.</param>
public readonly record struct TreeRun(string Text, TreeRunStyle Style, object? Link = null, TreeRunIcon Icon = TreeRunIcon.None);
