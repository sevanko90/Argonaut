using System;
using System.Collections.Generic;
using Argonaut.Engine.Hints;
using Argonaut.Ui.Rows;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Ui.Hints;

/// <summary>
/// One value hint drawn as a chip on its own - the same chip a row draws after its text, for a
/// place with no row to put it in, such as a popup by a word. Clicking it raises
/// <see cref="Click"/>.
/// </summary>
public sealed class HintChip : Control
{
    public static readonly StyledProperty<ValueHint?> HintProperty =
        AvaloniaProperty.Register<HintChip, ValueHint?>(nameof(Hint));

    public static readonly StyledProperty<IBrush?> ChipBackgroundProperty =
        AvaloniaProperty.Register<HintChip, IBrush?>(nameof(ChipBackground));

    public static readonly StyledProperty<IBrush?> ChipHoverBackgroundProperty =
        AvaloniaProperty.Register<HintChip, IBrush?>(nameof(ChipHoverBackground));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<HintChip, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<FontFamily?> FontFamilyProperty =
        AvaloniaProperty.Register<HintChip, FontFamily?>(nameof(FontFamily));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<HintChip, double>(nameof(FontSize), 12);

    private readonly ChipPainter painter = new();
    private TextLayout? label;
    private bool isHovered;

    static HintChip()
    {
        AffectsMeasure<HintChip>(HintProperty, FontFamilyProperty, FontSizeProperty);
        AffectsRender<HintChip>(ChipBackgroundProperty, ChipHoverBackgroundProperty, ForegroundProperty);
    }

    public HintChip()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public ValueHint? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public IBrush? ChipBackground
    {
        get => GetValue(ChipBackgroundProperty);
        set => SetValue(ChipBackgroundProperty, value);
    }

    public IBrush? ChipHoverBackground
    {
        get => GetValue(ChipHoverBackgroundProperty);
        set => SetValue(ChipHoverBackgroundProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily? FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>The glyph each icon is drawn with; a hint whose icon is missing shows none.</summary>
    public IReadOnlyDictionary<TreeRunIcon, Geometry>? Icons { get; set; }

    public event EventHandler? Click;

    /// <summary>Clicks the chip, as a pointer press on it does - for tests.</summary>
    internal void PerformClick() => Click?.Invoke(this, EventArgs.Empty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HintProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty
            || change.Property == ForegroundProperty)
            label = null;
        if (change.Property == ForegroundProperty)
            painter.ForgetPens();
    }

    private TextLayout? Label()
    {
        if (Hint is not { } hint)
            return null;

        return label ??= new TextLayout(hint.Text, new Typeface(FontFamily ?? FontFamily.Default), FontSize, Foreground ?? Brushes.Gray);
    }

    private Geometry? Icon()
        => Hint is { } hint && ValueHintLook.SwatchOf(hint) is null && Icons is not null
           && Icons.TryGetValue(ValueHintLook.IconOf(hint.Symbol), out var glyph) ? glyph : null;

    private IBrush? Swatch() => Hint is { } hint && ValueHintLook.SwatchOf(hint) is { } colour ? new ImmutableSolidColorBrush(colour) : null;

    protected override Size MeasureOverride(Size availableSize)
        => Label() is { } text ? new Size(ChipPainter.WidthOf(text, Icon() is not null || Swatch() is not null), RowSurface.RowHeight) : default;

    public override void Render(DrawingContext context)
    {
        if (Label() is not { } text)
            return;

        var fill = isHovered ? ChipHoverBackground ?? ChipBackground : ChipBackground;
        painter.Draw(context, 0, 0, Bounds.Width, text, Foreground ?? Brushes.Gray, fill, Icon(), Swatch());
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        isHovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        isHovered = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Click?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
