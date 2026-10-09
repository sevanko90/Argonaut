using System;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Raw;

/// <summary>
/// Header toolbar for the raw viewer: the wrap-width combo, the colour picker and the edit-mode toggle. Reports the
/// wrap choice to the owning document, which remembers it and applies it via
/// <see cref="RawViewModel.SetWrapWidth"/> (a re-index); the toggle goes to
/// <see cref="RawViewModel.SetEditing"/>. Owned by the document view model that creates it and
/// shares its lifetime.
/// </summary>
public sealed class RawToolbarViewModel : ObservableObject
{
    private readonly Action<int> applyWrapWidth;
    private readonly Action<bool> applyEditing;
    private readonly Action<RawColourChoice>? applyColours;
    private int coloursIndex;
    private string autoColoursLabel = AutoLabel(null);
    private int wrapWidthIndex;
    private bool canEdit;
    private bool isEditing;
    private bool canChangeWrapWidth = true;

    public RawToolbarViewModel(
        int initialWrapWidth, Action<int> applyWrapWidth, Action<bool> applyEditing, Action<RawColourChoice>? applyColours = null)
    {
        this.applyColours = applyColours;
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

    /// <summary>
    /// Bound two-way to the colour picker; its order is <see cref="RawColourChoice"/>'s. Only
    /// records the choice: acting on it is handed to <see cref="UiDeferral.AfterCurrentInput"/>,
    /// because a setter run inside the selection commit must not change what the control is
    /// still committing against. Swapping the lexer does not close the popup today, but the
    /// rule is not worth finding the exception to.
    /// </summary>
    public int ColoursIndex
    {
        get => coloursIndex;
        set
        {
            if (value < 0 || value > (int)RawColourChoice.Config || !SetField(ref coloursIndex, value))
                return;

            var choice = (RawColourChoice)value;
            UiDeferral.AfterCurrentInput(() => applyColours?.Invoke(choice));
        }
    }

    /// <summary>The picker's first entry, which names what Auto chose.</summary>
    public string AutoColoursLabel
    {
        get => autoColoursLabel;
        private set => SetField(ref autoColoursLabel, value);
    }

    /// <summary>Tells the picker what Auto chose; null for no lexer.</summary>
    public void SetAutoColours(string? lexerName) => AutoColoursLabel = AutoLabel(lexerName);

    private static string AutoLabel(string? lexerName) => $"Colours: Auto ({lexerName ?? "none"})";

    /// <summary>Whether the edit toggle is usable yet - false until the background scan finishes,
    /// which is what an edited row index requires.</summary>
    public bool CanEdit
    {
        get => canEdit;
        set => SetField(ref canEdit, value);
    }

    /// <summary>
    /// Whether re-wrapping is allowed right now: not while a save is writing the document out,
    /// which is reading the bytes a re-wrap would be scanning.
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
