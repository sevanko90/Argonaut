using System;
using System.Globalization;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// The struct tree's offset column: each row's offset from the struct's start, in decimal as
/// <c>offsetof</c> gives it - <c>+9</c>, or <c>+9.3</c> for a bitfield starting at bit 3 - so the
/// layout can be checked against a header at a glance. The document offset is the dump's to show;
/// the row's tooltip gives it in hex.
/// </summary>
public sealed class StructOffsetGutter(StructTree tree) : ITreeGutter
{
    private const int Digits = 7;

    /// <summary>One character's width in the surface's font, set by the view so the column fits
    /// <c>+999999</c>.</summary>
    public double CharacterWidth { get; set; } = 7.5;

    public double Width => CharacterWidth * Digits + 12;

    public void Draw(DrawingContext context, in TreeRow row, Rect cell, in TreeGutterStyle style)
    {
        if (row.Detail is not StructNode node || node.Kind == StructNodeKind.Root)
            return;

        using var layout = new TextLayout(Text(node), style.Typeface, style.FontSize, style.Brush, TextAlignment.Right,
            maxWidth: Math.Max(0, cell.Width - 8));
        layout.Draw(context, new Point(cell.X, cell.Y + Math.Max(0, (cell.Height - layout.Height) / 2)));
    }

    public object? ToolTipFor(in TreeRow row)
        => row.Detail is StructNode node ? $"0x{tree.Start + node.Offset:X} in the document" : null;

    internal static string Text(in StructNode node)
    {
        string offset = "+" + node.Offset.ToString(CultureInfo.InvariantCulture);
        return node.Value.BitWidth is not null && node.Kind == StructNodeKind.Field ? $"{offset}.{node.Value.BitOffset}" : offset;
    }
}
