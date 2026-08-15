using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Argonaut.Features.Json;

/// <summary>
/// Turns a row's nesting depth into a left indent. Configured per use site rather than fixed,
/// because the tree and the schema gutter want very different steps: the tree has the whole
/// viewport (and a horizontal scrollbar) to spend, while the gutter is a narrow, user-resizable
/// column whose labels are already ellipsed, so it needs a smaller step and a ceiling - past a
/// certain depth the indent would cost more label than the extra nesting cue is worth.
/// </summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public double IndentPerLevel { get; set; } = 16;

    /// <summary>Indent ceiling in px. Deeper rows all sit at this indent - they stop stepping
    /// right, but stay aligned with each other.</summary>
    public double MaxIndent { get; set; } = double.PositiveInfinity;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int depth = value is int d ? d : 0;
        return new Thickness(Math.Min(depth * IndentPerLevel, MaxIndent), 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
