using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Argonaut.Ui.Icons;

/// <summary>
/// Draws one icon from <c>LucideIcons.axaml</c> at <see cref="Size"/> pixels square. Lucide icons
/// are outlines on a 24x24 grid meant to be stroked, which a <c>PathIcon</c> cannot do - it only
/// fills - so this scales the geometry and strokes it with round caps and joins, as Lucide's own
/// renderers do.
///
/// <see cref="Foreground"/> is the inherited text foreground, so an icon inside a button takes the
/// button's colour (a checked toggle's accent included) unless a style says otherwise.
/// </summary>
public sealed class LucideIcon : Control
{
    /// <summary>Lucide's own grid; every geometry in the dictionary is drawn on it.</summary>
    private const double GridSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(Size), 15);

    /// <summary>Line weight in grid units, so it scales with <see cref="Size"/>: 1.8 at 15px
    /// draws a little over a pixel, matching the weight of the 12-13px interface text.</summary>
    public static readonly StyledProperty<double> StrokeWeightProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(StrokeWeight), 1.8);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LucideIcon>();

    private Pen? pen;

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty, StrokeWeightProperty, SizeProperty);
        AffectsMeasure<LucideIcon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeWeight
    {
        get => GetValue(StrokeWeightProperty);
        set => SetValue(StrokeWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ForegroundProperty || change.Property == StrokeWeightProperty)
            pen = null;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry || Foreground is not { } brush)
            return;

        pen ??= new Pen(brush, StrokeWeight, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        double scale = Size / GridSize;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, geometry);
    }
}
