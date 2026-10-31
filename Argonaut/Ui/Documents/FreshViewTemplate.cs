using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace Argonaut.Ui.Documents;

/// <summary>
/// Wraps a data template so every new piece of content gets a new view, never the previous
/// content's view handed the new content.
///
/// A XAML <c>DataTemplate</c> recycles: when a content presenter's content changes to another
/// object the same template matches, it keeps the view it built and lets the new content flow in
/// as its data context. The document views do not survive that - the view a replaced document
/// left behind is not told its data context changed, so it keeps drawing the disposed document
/// (a second text file opened over a first one showed a blank raw view). This type does not
/// implement <see cref="IRecyclingDataTemplate"/>, so the presenter builds a view per document,
/// and the outgoing view leaves the tree with the document it was built for.
/// </summary>
public sealed class FreshViewTemplate : IDataTemplate
{
    /// <summary>The template that builds each view.</summary>
    [Content]
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data) => Template?.Match(data) ?? false;
}
