using System.Collections.Generic;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// The struct tree's colours, the JSON tree's for the same roles - names in the accent, values by
/// type, types and padding muted - so a decoded struct reads like any other tree in the app.
/// Looked up from the theme, so a theme change is one call again.
/// </summary>
public static class StructTreePalette
{
    public static void Apply(TreeSurface surface, Control resourceHost)
    {
        var brushes = new Dictionary<TreeRunStyle, IBrush>();
        void Add(TreeRunStyle style, string key)
        {
            if (resourceHost.TryFindResource(key, resourceHost.ActualThemeVariant, out var found) && found is IBrush brush)
                brushes[style] = brush;
        }

        Add(TreeRunStyle.Name, "AppAccentBrush");
        Add(TreeRunStyle.String, "AppJsonStringBrush");
        Add(TreeRunStyle.Number, "AppJsonNumberBrush");
        Add(TreeRunStyle.Keyword, "AppJsonBoolBrush");
        Add(TreeRunStyle.Comment, "AppMutedTextBrush");
        Add(TreeRunStyle.Note, "AppSubtleIconBrush");
        Add(TreeRunStyle.Hint, "AppSubtleIconBrush");
        surface.RunBrushes = brushes;
    }
}
