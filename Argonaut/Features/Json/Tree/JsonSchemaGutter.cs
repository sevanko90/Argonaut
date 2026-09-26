using System;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Features.Json.Tree;

/// <summary>
/// The schema gutter beside the JSON tree: for each row the bound schema describes, its title (or
/// an enum member's label, or the first line of its description), with the full title and
/// description on hover. Zero width while no schema is bound, so an unbound document gives up no
/// space to it; resizable by dragging its edge, and the width is kept across a schema being
/// unbound and bound again.
/// </summary>
public sealed class JsonSchemaGutter(JsonSchemaResolver resolver, JsonTreeText text) : IResizableTreeGutter
{
    private const double DefaultWidth = 220;

    /// <summary>Labels step in with their row, so a child reads as belonging to the container
    /// above it - by much less than the tree, and capped, because the gutter is narrow.</summary>
    private const double IndentPerLevel = 6;
    private const double MaxIndent = 60;

    private double width = DefaultWidth;
    private (long, bool) toolTipKey = (-1, false);
    private object? toolTip;

    public double Width => resolver.Schema is null ? 0 : width;

    public double MinWidth => 40;

    public void Resize(double newWidth) => width = Math.Max(MinWidth, newWidth);

    public void Draw(DrawingContext context, in TreeRow row, Rect cell, in TreeGutterStyle style)
    {
        if (Describe(row) is not { Label: { } label })
            return;

        double indent = Math.Min(row.Depth * IndentPerLevel, MaxIndent);
        var weight = row.Shape == TreeRowShape.Open ? FontWeight.SemiBold : FontWeight.Normal;
        var layout = new TextLayout(label, new Typeface(style.Typeface.FontFamily, weight: weight), style.FontSize, style.Brush,
            textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Math.Max(0, cell.Width - indent - 4));
        layout.Draw(context, new Point(cell.X + indent, cell.Y + Math.Max(0, (cell.Height - layout.Height) / 2)));
    }

    public object? ToolTipFor(in TreeRow row)
    {
        // The same object for the same row, so the surface can tell the pointer has not moved to
        // a new cell and leave an open tooltip alone.
        if (row.Key == toolTipKey)
            return toolTip;

        toolTipKey = row.Key;
        toolTip = Describe(row) is { } described ? BuildToolTip(described.Title, described.Description) : null;
        return toolTip;
    }

    /// <summary>The schema's words for this row: title and description (an enum member's, for a
    /// value the schema enumerates), and the label the gutter shows - null when it says nothing.</summary>
    internal (string? Label, string? Title, string? Description)? Describe(in TreeRow row)
    {
        if (resolver.Schema is not { } schema || resolver.NodeFor(row) is var node && node < 0)
            return null;

        string? title = schema.GetTitle(node);
        string? description = schema.GetDescription(node);

        // A matched enum member's label says more here than the property's own title.
        if (row.Shape == TreeRowShape.Leaf)
        {
            string value = text.Scalar(row, out _, out _, out _);
            if (schema.TryGetEnumLabel(node, value, (JsonTokenKind)row.Node.FormatKind, out var enumTitle, out var enumDescription))
            {
                title = enumTitle ?? title;
                description = enumDescription ?? description;
            }
        }

        string? label = title ?? JsonSchemaLabel.FirstLine(description);
        return label is null ? null : (label, title, description);
    }

    private static Control BuildToolTip(string? title, string? description)
    {
        var panel = new StackPanel { MaxWidth = 420 };
        if (title is not null)
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (title is not null && description is not null)
            panel.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6), Background = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Stretch });
        if (description is not null)
            panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
}
