using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Argonaut.Features.Json;

public sealed class DepthToMarginConverter : IValueConverter
{
    private const double IndentPerLevel = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int depth = value is int d ? d : 0;
        return new Thickness(depth * IndentPerLevel, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
