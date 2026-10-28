using System;
using Argonaut.Ui.Tree;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Xml;

/// <summary>
/// Header toolbar for the XML tree view: the shared tree controls (expand depth, indent guides)
/// and nothing else yet.
/// </summary>
public sealed class XmlToolbarViewModel : ObservableObject
{
    public XmlToolbarViewModel(int initialExpandDepth, Action<int> applyExpandDepth, bool showIndentGuides = false,
        Action<bool>? applyIndentGuides = null)
    {
        TreeControls = new TreeToolbarState(initialExpandDepth, applyExpandDepth, showIndentGuides, applyIndentGuides);
    }

    /// <summary>The expand-depth and indent-guides controls every tree toolbar shares.</summary>
    public TreeToolbarState TreeControls { get; }
}
