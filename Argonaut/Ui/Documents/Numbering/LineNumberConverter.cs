using System;
using System.Collections.Generic;
using System.Globalization;
using Argonaut.Engine.Indexing.Lines;
using Avalonia.Data.Converters;

namespace Argonaut.Ui.Documents.Numbering;

/// <summary>
/// Shows a templated row's 1-based line number under its document's <see cref="LineNumbering"/>:
/// bound to the row's own number and the document's numbering, so flipping the numbering
/// renumbers the realized rows without rebuilding them.
/// </summary>
public sealed class LineNumberConverter : IMultiValueConverter
{
    public static readonly LineNumberConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1 || values[0] is not int lineNumber)
            return null;

        var numbering = values.Count > 1 && values[1] is LineNumbering n ? n : LineNumbering.FromStart;
        return numbering.Line(lineNumber).ToString(CultureInfo.InvariantCulture);
    }
}
