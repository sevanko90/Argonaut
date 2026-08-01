using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Argonaut.Features.Json;

// Vector geometry instead of a ▶/▼ text glyph: text-glyph triangles pick up
// per-character font-fallback metrics that differ by platform (and even by
// glyph within the same fallback chain on Windows), so collapsed/expanded
// triangles rendered at visibly different sizes there. Geometry always
// renders at the same fixed size regardless of OS or font fallback.
public sealed class ExpandGlyphConverter : IValueConverter
{
    public static readonly ExpandGlyphConverter Instance = new();

    private static readonly Geometry Collapsed = Geometry.Parse("M1,0 L1,7 L6,3.5 Z");
    private static readonly Geometry Expanded = Geometry.Parse("M0,1 L7,1 L3.5,6 Z");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Expanded : Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IsNotNullConverter : IValueConverter
{
    public static readonly IsNotNullConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IsNullConverter : IValueConverter
{
    public static readonly IsNullConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Binds one RadioButton in a group to a single value of an int-backed selection (e.g. a
/// DateDecodingScheme/DateHintTimeZoneMode index), via ConverterParameter carrying that
/// button's value as a string. ConvertBack ignores the "unchecked" (false) notification that
/// fires on every other button in the group when one gets checked - only the newly-checked
/// button's true should push a new value back.
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && parameter is string s && int.TryParse(s, out int target) && i == target;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string s && int.TryParse(s, out int target))
            return target;

        return BindingOperations.DoNothing;
    }
}
