using System.Collections.Generic;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// The XML tree's colours: which theme brush each run style draws in - element names in the
/// accent, attribute names and values and text in the colours JSON gives numbers and strings,
/// punctuation and comments quiet. Looked up from the theme so a theme change is one call. These
/// are starting points; the look is settled from screenshots.
/// </summary>
public static class XmlTreePalette
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
        Add(TreeRunStyle.AttributeName, "AppJsonNumberBrush");
        Add(TreeRunStyle.AttributeValue, "AppJsonStringBrush");
        Add(TreeRunStyle.String, "AppJsonStringBrush");
        Add(TreeRunStyle.Keyword, "AppJsonBoolBrush");
        Add(TreeRunStyle.Punctuation, "AppSubtleIconBrush");
        Add(TreeRunStyle.Comment, "AppMutedTextBrush");
        Add(TreeRunStyle.Summary, "AppMutedTextBrush");
        Add(TreeRunStyle.Hint, "AppMutedTextBrush");
        Add(TreeRunStyle.Link, "AppMutedTextBrush");
        Add(TreeRunStyle.Note, "AppSubtleIconBrush");
        Add(TreeRunStyle.Chip, "AppMutedTextBrush");
        Add(TreeRunStyle.Action, "AppMutedTextBrush");
        surface.RunBrushes = brushes;

        surface.RunIcons = TreeRunIcons.Load(resourceHost);
    }
}
