using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Header toolbar for the raw viewer: the wrap-width combo. Persists the choice and applies
/// it to the owning document via <see cref="RawViewModel.SetWrapWidth"/> (a re-index). Owned
/// by the document view model that creates it and shares its lifetime.
/// </summary>
public sealed class RawToolbarViewModel : ObservableObject
{
    private readonly Action<int> applyWrapWidth;
    private int wrapWidthIndex;

    public RawToolbarViewModel(int initialWrapWidth, Action<int> applyWrapWidth)
    {
        this.applyWrapWidth = applyWrapWidth;

        wrapWidthIndex = Array.IndexOf(RawWrapWidthPreference.Widths, initialWrapWidth);
        if (wrapWidthIndex < 0)
            wrapWidthIndex = Array.IndexOf(RawWrapWidthPreference.Widths, RawWrapWidthPreference.Default);
    }

    /// <summary>Bound two-way to the wrap-width combo. The &lt; 0 guard absorbs the -1 a
    /// ComboBox raises during teardown (see JsonToolbarViewModel's combo setters).</summary>
    public int WrapWidthIndex
    {
        get => wrapWidthIndex;
        set
        {
            if (value < 0 || value >= RawWrapWidthPreference.Widths.Length || !SetField(ref wrapWidthIndex, value))
                return;

            int width = RawWrapWidthPreference.Widths[value];
            RawWrapWidthPreference.Save(width);
            applyWrapWidth(width);
        }
    }
}
