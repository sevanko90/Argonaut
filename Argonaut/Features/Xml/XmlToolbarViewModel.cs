using System;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Tree;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Xml;

/// <summary>
/// Header toolbar for the XML tree view: the shared tree controls (expand depth, indent guides)
/// and the date-hint choices.
/// </summary>
public sealed class XmlToolbarViewModel : ObservableObject
{
    public XmlToolbarViewModel(DateHintSettings hintSettings, int initialExpandDepth, Action<int> applyExpandDepth, bool showIndentGuides = false,
        Action<bool>? applyIndentGuides = null)
    {
        HintSettings = hintSettings;
        DateHintControls = new DateHintToolbarState(hintSettings);
        TreeControls = new TreeToolbarState(initialExpandDepth, applyExpandDepth, showIndentGuides, applyIndentGuides);
    }

    /// <summary>The document's date-hint settings, which the date controls edit.</summary>
    public DateHintSettings HintSettings { get; }

    /// <summary>The date-hint scheme and time-zone choices every tree toolbar shares.</summary>
    public DateHintToolbarState DateHintControls { get; }

    /// <summary>The expand-depth and indent-guides controls every tree toolbar shares.</summary>
    public TreeToolbarState TreeControls { get; }
}
