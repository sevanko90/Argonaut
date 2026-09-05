using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Argonaut.Infrastructure;

/// <summary>
/// How many pixels a grid cell needs for a given number of characters - both terms measured
/// rather than guessed.
///
/// The grid widths a column from a character count (see <c>CsvStructure</c>): counting is cheap
/// on a multi-gigabyte file where laying out every value's text is not. Turning that count into
/// pixels needs two numbers, and hard-coding either has now caused the same bug twice - values
/// trimmed to "66317..." in a column that was supposed to fit "6631700":
///
///   * <see cref="CharAdvance"/> - the width of one character in the content font. Measured from
///     the resolved typeface at the resolved size, because it is a property of whichever face
///     the platform picked out of <c>AppContentFontFamily</c> (SF Mono and Menlo advance at
///     0.6em, Consolas narrower), not a number this code gets to choose.
///   * <see cref="CellInset"/> - the horizontal chrome a cell wraps around its text: the cell
///     theme's padding plus its column separator. Only a realized cell knows it (the theme's
///     padding is a dynamic resource, and an unattached cell never applies its theme at all), so
///     it is learned from the first one and reported back through
///     <see cref="ReportCellInset"/>.
///
/// <see cref="Current"/> is a settable seam, the same shape as <see cref="AppDataPaths.RootOverride"/>
/// and <see cref="UiDeferral.PostOverride"/>: tests that run without an Avalonia platform can
/// state the metrics they want to assert against instead of measuring a font that isn't there.
/// </summary>
public sealed class CellTextMetrics
{
    /// <summary>Long enough that any per-string overhead in the measurement is lost in the
    /// division, short enough to be free.</summary>
    private const int SampleLength = 32;

    /// <summary>The characters the advance is taken from. A monospace face - which is what
    /// <c>AppContentFontFamily</c> asks for - gives the same answer for all of them; on a
    /// proportional fallback the widest wins, so a column errs wide rather than trimming.</summary>
    private static readonly char[] WidestCharacters = ['0', 'W', 'm', '{'];

    private static CellTextMetrics? current;

    public CellTextMetrics(double charAdvance, double cellInset)
    {
        CharAdvance = charAdvance;
        CellInset = cellInset;
    }

    /// <summary>Width of one character of cell text, in pixels.</summary>
    public double CharAdvance { get; }

    /// <summary>Horizontal pixels a cell spends on chrome before any text is drawn.</summary>
    public double CellInset { get; }

    /// <summary>
    /// The metrics in force. Measured from the running application's content font on first use;
    /// set explicitly to pin them (tests), which also skips the measurement.
    /// </summary>
    public static CellTextMetrics Current
    {
        get => current ??= MeasureFont();
        set => current = value;
    }

    /// <summary>Pixels needed to render <paramref name="chars"/> characters in a cell.</summary>
    public double WidthForChars(int chars) => chars * CharAdvance + CellInset;

    /// <summary>
    /// Drops the measured advance so the next request re-measures - what the status bar's
    /// content-font toggle needs, since a different face means a different advance. The learned
    /// cell inset is kept: it is the theme's chrome, and the font has nothing to do with it.
    /// </summary>
    public static void InvalidateFont()
    {
        double inset = Current.CellInset;
        current = new CellTextMetrics(MeasureFont().CharAdvance, inset);
    }

    /// <summary>
    /// Records the chrome a realized cell was measured to add. Returns true when this is news -
    /// the caller then re-applies the widths it seeded from the previous, un-measured value.
    /// </summary>
    public static bool ReportCellInset(double inset)
    {
        if (inset < 0 || Math.Abs(Current.CellInset - inset) < 0.5)
            return false;

        current = new CellTextMetrics(Current.CharAdvance, inset);
        return true;
    }

    /// <summary>
    /// The content font's advance, with no cell inset yet - the first realized cell reports that.
    ///
    /// Without a platform (a view-model test that never starts one) nothing can be measured, and
    /// the fallback is one em per character: the em square is the font size by definition, so no
    /// face can exceed it and no text can be trimmed. Wide, but wide is recoverable and trimmed
    /// text is the bug this type exists to prevent.
    /// </summary>
    private static CellTextMetrics MeasureFont()
    {
        double fontSize = Resource<double>("AppContentFontSize", 12.0);

        try
        {
            var typeface = new Typeface(Resource<FontFamily>("AppContentFontFamily", FontFamily.Default));
            double widest = 0;

            foreach (char character in WidestCharacters)
            {
                var sample = new FormattedText(new string(character, SampleLength), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);
                widest = Math.Max(widest, sample.Width / SampleLength);
            }

            if (widest > 0)
                return new CellTextMetrics(widest, cellInset: 0);
        }
        catch (Exception)
        {
            // No font manager (no platform): fall through to the em-square budget.
        }

        return new CellTextMetrics(fontSize, cellInset: 0);
    }

    private static T Resource<T>(string key, T fallback)
        => Application.Current?.TryFindResource(key, out object? found) == true && found is T typed ? typed : fallback;
}
