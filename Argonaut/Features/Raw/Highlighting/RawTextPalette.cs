using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// The raw view's colours: which theme brush each text style draws in. Looked up from the theme
/// so a theme change is one call again. The brushes are the JSON tree's, shared by resource key
/// rather than by code, so the two views agree without one referencing the other.
/// </summary>
public static class RawTextPalette
{
    public static void Apply(RawTextSurface surface, Control resourceHost)
    {
        var brushes = new Dictionary<RawTextStyle, IBrush>();
        void Add(RawTextStyle style, string key)
        {
            if (resourceHost.TryFindResource(key, resourceHost.ActualThemeVariant, out var found) && found is IBrush brush)
                brushes[style] = brush;
        }

        Add(RawTextStyle.Key, "AppAccentBrush");
        Add(RawTextStyle.Section, "AppAccentBrush");
        Add(RawTextStyle.String, "AppJsonStringBrush");
        Add(RawTextStyle.Number, "AppJsonNumberBrush");
        Add(RawTextStyle.Keyword, "AppJsonBoolBrush");
        Add(RawTextStyle.Literal, "AppJsonNullBrush");
        Add(RawTextStyle.Comment, "AppMutedTextBrush");
        Add(RawTextStyle.Punctuation, "AppMutedTextBrush");
        surface.StyleBrushes = brushes;
    }
}
