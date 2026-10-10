using System.Collections.Generic;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argonaut.Features.Json.Tree;

/// <summary>
/// The JSON tree's colours: which theme brush each run style draws in - names in the accent,
/// values by type, notes and links muted - and the icons chips lead with. Looked up from the theme so a theme change is one call
/// again; every surface showing JSON uses this, so the tree and the table's cell pane agree.
/// </summary>
public static class JsonTreePalette
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
        Add(TreeRunStyle.Literal, "AppJsonNullBrush");
        Add(TreeRunStyle.Hint, "AppMutedTextBrush");
        Add(TreeRunStyle.Link, "AppMutedTextBrush");
        Add(TreeRunStyle.Comment, "AppMutedTextBrush");
        Add(TreeRunStyle.Note, "AppSubtleIconBrush");
        Add(TreeRunStyle.Chip, "AppMutedTextBrush");
        Add(TreeRunStyle.Action, "AppMutedTextBrush");
        surface.RunBrushes = brushes;

        var icons = new Dictionary<TreeRunIcon, Geometry>();
        void AddIcon(TreeRunIcon icon, string key)
        {
            if (resourceHost.TryFindResource(key, out var found) && found is Geometry glyph)
                icons[icon] = glyph;
        }

        AddIcon(TreeRunIcon.Time, "Icon.Hints");
        AddIcon(TreeRunIcon.Table, "Icon.Table");
        AddIcon(TreeRunIcon.FullText, "Icon.FullText");
        AddIcon(TreeRunIcon.Key, "Icon.Key");
        AddIcon(TreeRunIcon.OpenLink, "Icon.OpenLink");
        AddIcon(TreeRunIcon.Mail, "Icon.Mail");
        AddIcon(TreeRunIcon.Json, "Icon.Json");
        AddIcon(TreeRunIcon.Binary, "Icon.Binary");
        AddIcon(TreeRunIcon.Image, "Icon.Image");
        surface.RunIcons = icons;
    }
}
