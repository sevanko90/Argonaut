using System;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Raw;

/// <summary>
/// Header toolbar for the raw viewer: the wrap-width combo and the edit-mode toggle. Reports the
/// wrap choice to the owning document, which remembers it and applies it via
/// <see cref="RawViewModel.SetWrapWidth"/> (a re-index); the toggle goes to
/// <see cref="RawViewModel.SetEditing"/>. Owned by the document view model that creates it and
/// shares its lifetime.
/// </summary>
public sealed class RawToolbarViewModel : ObservableObject
{
    private readonly Action<int> applyWrapWidth;
    private readonly Action<bool> applyEditing;
    private int wrapWidthIndex;
    private bool canEdit;
    private bool isEditing;
    private bool canChangeWrapWidth = true;

    public RawToolbarViewModel(int initialWrapWidth, Action<int> applyWrapWidth, Action<bool> applyEditing)
    {
        this.applyWrapWidth = applyWrapWidth;
        this.applyEditing = applyEditing;

        wrapWidthIndex = Array.IndexOf(RawViewSettings.Widths, initialWrapWidth);
        if (wrapWidthIndex < 0)
            wrapWidthIndex = Array.IndexOf(RawViewSettings.Widths, RawViewSettings.DefaultWrapWidth);
    }

    /// <summary>Bound two-way to the wrap-width combo. The &lt; 0 guard absorbs the -1 a
    /// ComboBox raises during teardown (see JsonToolbarViewModel's combo setters).</summary>
    public int WrapWidthIndex
    {
        get => wrapWidthIndex;
        set
        {
            if (value < 0 || value >= RawViewSettings.Widths.Length || !SetField(ref wrapWidthIndex, value))
                return;

            applyWrapWidth(RawViewSettings.Widths[value]);
        }
    }

    /// <summary>Whether the edit toggle is usable yet - false until the background scan finishes,
    /// which is what an edited row index requires.</summary>
    public bool CanEdit
    {
        get => canEdit;
        set => SetField(ref canEdit, value);
    }

    /// <summary>
    /// Whether re-wrapping is still allowed. Goes false once a piece table exists, because
    /// re-wrapping an edited document means re-scanning the edited bytes.
    /// </summary>
    public bool CanChangeWrapWidth
    {
        get => canChangeWrapWidth;
        set => SetField(ref canChangeWrapWidth, value);
    }

    /// <summary>
    /// Bound two-way to the edit toggle. The document has the last word - it can refuse, and
    /// writes the answer back through <see cref="RawViewModel.SetEditing"/> - so this setter
    /// only reports the click and lets the field be corrected afterwards.
    /// </summary>
    public bool IsEditing
    {
        get => isEditing;
        set
        {
            if (!SetField(ref isEditing, value))
                return;

            applyEditing(value);
        }
    }
}
