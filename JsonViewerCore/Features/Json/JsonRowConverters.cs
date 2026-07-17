using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JsonViewerCore.Features.Json;

public sealed class ExpandGlyphConverter : IValueConverter
{
    public static readonly ExpandGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▼" : "▶";

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
