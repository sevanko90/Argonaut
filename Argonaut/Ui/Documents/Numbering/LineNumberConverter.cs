using System;
using System.Collections.Generic;
using System.Globalization;
using Argonaut.Engine.Indexing.Lines;
using Avalonia.Data.Converters;

namespace Argonaut.Ui.Documents.Numbering;

/// <summary>
/// Shows a templated row's gutter label under its document's <see cref="LineNumbering"/>: bound
/// to the row's own 1-based number, the document's numbering and the row's start offset, so
/// flipping the numbering relabels the realized rows without rebuilding them.
/// </summary>
public sealed class LineNumberConverter : IMultiValueConverter
{
    public static readonly LineNumberConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1 || values[0] is not int lineNumber)
            return null;

        var numbering = values.Count > 1 && values[1] is LineNumbering n ? n : LineNumbering.FromStart;
        long lineStart = values.Count > 2 && values[2] is long start ? start : 0;
        return numbering.GutterLabel(lineNumber, lineStart);
    }
}
