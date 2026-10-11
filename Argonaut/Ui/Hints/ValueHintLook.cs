using Argonaut.Engine.Hints;
using Argonaut.Ui.Tree;
using Avalonia.Media;

namespace Argonaut.Ui.Hints;

/// <summary>How a value hint is drawn, the same in every view: its icon, its swatch, and whether
/// it is a chip or an action.</summary>
public static class ValueHintLook
{
    public static TreeRunIcon IconOf(ValueHintSymbol symbol) => symbol switch
    {
        ValueHintSymbol.Time => TreeRunIcon.Time,
        ValueHintSymbol.Key => TreeRunIcon.Key,
        ValueHintSymbol.OpenLink => TreeRunIcon.OpenLink,
        ValueHintSymbol.Mail => TreeRunIcon.Mail,
        ValueHintSymbol.Json => TreeRunIcon.Json,
        ValueHintSymbol.Binary => TreeRunIcon.Binary,
        ValueHintSymbol.Image => TreeRunIcon.Image,
        _ => TreeRunIcon.None,
    };

    public static Color? SwatchOf(ValueHint hint) => hint.Colour is { } argb ? Color.FromUInt32(argb) : null;

    public static TreeRunStyle StyleOf(ValueHint hint) => hint.Emphasis == ValueHintEmphasis.Action ? TreeRunStyle.Action : TreeRunStyle.Chip;
}
