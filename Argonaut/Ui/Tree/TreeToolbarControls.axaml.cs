using Avalonia.Controls;

namespace Argonaut.Ui.Tree;

/// <summary>The expand-depth button with its flyout and the indent-guides toggle, over a
/// <see cref="TreeToolbarState"/>. Hosted by each tree format's toolbar.</summary>
public partial class TreeToolbarControls : UserControl
{
    public TreeToolbarControls()
    {
        InitializeComponent();
    }
}
