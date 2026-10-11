using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argonaut.Ui.Tree;

/// <summary>The glyph each <see cref="TreeRunIcon"/> is drawn with, from the theme's icon
/// resources - shared, so a chip leads with the same icon in every view.</summary>
public static class TreeRunIcons
{
    public static Dictionary<TreeRunIcon, Geometry> Load(Control resourceHost)
    {
        var icons = new Dictionary<TreeRunIcon, Geometry>();
        void Add(TreeRunIcon icon, string key)
        {
            if (resourceHost.TryFindResource(key, out var found) && found is Geometry glyph)
                icons[icon] = glyph;
        }

        Add(TreeRunIcon.Time, "Icon.Hints");
        Add(TreeRunIcon.Table, "Icon.Table");
        Add(TreeRunIcon.FullText, "Icon.FullText");
        Add(TreeRunIcon.Key, "Icon.Key");
        Add(TreeRunIcon.OpenLink, "Icon.OpenLink");
        Add(TreeRunIcon.Mail, "Icon.Mail");
        Add(TreeRunIcon.Json, "Icon.Json");
        Add(TreeRunIcon.Binary, "Icon.Binary");
        Add(TreeRunIcon.Image, "Icon.Image");
        Add(TreeRunIcon.ChevronDown, "Icon.ChevronDown");
        return icons;
    }
}
