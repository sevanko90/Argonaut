using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Argonaut.Ui.Converters;

/// <summary>
/// Binds one RadioButton in a group to a single value of an int-backed selection (a date-hint
/// scheme, an expand depth, a wrap width), via ConverterParameter carrying that
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
