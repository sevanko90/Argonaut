namespace Argonaut.Ui.Tree;

/// <summary>What a tree view remembers between documents: how far a new tree opens and whether
/// it draws indent guides. Declared here because the tree view model reads and writes it; each
/// format's settings block implements it from above.</summary>
public interface ITreeViewSettings
{
    /// <summary>How many levels a newly opened tree starts expanded to.</summary>
    int ExpandDepth { get; set; }

    /// <summary>Whether the tree draws a faint line down each open container's children.</summary>
    bool ShowIndentGuides { get; set; }
}
