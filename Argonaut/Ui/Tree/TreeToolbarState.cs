using System;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Ui.Tree;

/// <summary>
/// The state behind <see cref="TreeToolbarControls"/>: how many levels a tree opens expanded to,
/// and whether it draws indent guides. A toolbar view model owns one and hosts the controls on
/// it; each choice is reported to the owning document, which remembers it and applies it live
/// to its tree.
/// </summary>
public sealed class TreeToolbarState : ObservableObject
{
    private readonly Action<int> applyExpandDepth;
    private readonly Action<bool>? applyIndentGuides;
    private int expandDepthIndex;
    private bool showIndentGuides;

    public TreeToolbarState(int initialExpandDepth, Action<int> applyExpandDepth, bool showIndentGuides = false,
        Action<bool>? applyIndentGuides = null)
    {
        expandDepthIndex = initialExpandDepth;
        this.applyExpandDepth = applyExpandDepth;
        this.showIndentGuides = showIndentGuides;
        this.applyIndentGuides = applyIndentGuides;
    }

    /// <summary>Bound two-way to the expand-depth choices; the value is the depth itself. Reports the choice to the owning
    /// document, which remembers it and applies it live to its tree.</summary>
    public int ExpandDepthIndex
    {
        get => expandDepthIndex;
        set
        {
            if (value < 0 || !SetField(ref expandDepthIndex, value))
                return;

            applyExpandDepth(value);
        }
    }

    /// <summary>Bound two-way to the indent-guides toggle. Reports the choice to the owning
    /// document, which remembers it and applies it live to its tree.</summary>
    public bool ShowIndentGuides
    {
        get => showIndentGuides;
        set
        {
            if (!SetField(ref showIndentGuides, value))
                return;

            applyIndentGuides?.Invoke(value);
        }
    }
}
