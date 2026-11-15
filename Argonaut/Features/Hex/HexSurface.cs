using System;
using System.Collections.Generic;
using Argonaut.Ui.Rows;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace Argonaut.Features.Hex;

/// <summary>The bytes a <see cref="HexSurface"/> draws: how many there are now, and one row's
/// worth on request. Read on the UI thread as rows come on screen.</summary>
public interface IHexDocument
{
    /// <summary>How many bytes there are now - read each time, never kept, as a streamed
    /// document grows.</summary>
    long Length { get; }

    /// <summary>Copies row <paramref name="row"/>'s bytes into <paramref name="into"/> (16 long),
    /// returning how many there were: 16, fewer on the last row, 0 past the end or once the
    /// document has been let go.</summary>
    int ReadRow(long row, Span<byte> into);
}

/// <summary>
/// Draws a document as a hex dump - offset, sixteen bytes, their characters - one fixed-height row
/// at a time, reading only the rows on screen. The row count is arithmetic on the length
/// (<see cref="HexRows"/>), so the scroll model is <see cref="RowSurface"/>'s exact one, as the raw
/// view's is, and a multi-gigabyte document scrolls as a small one does.
///
/// Bytes are coloured by class: zero muted (it is most of a binary file's padding), printable ASCII
/// as text, everything else in <see cref="OtherByteForeground"/>, so structure stands out. Layouts
/// are kept for the rows on screen only, and dropped as they scroll away.
/// </summary>
public sealed class HexSurface : RowSurface
{
    public static readonly StyledProperty<IBrush?> OtherByteForegroundProperty =
        AvaloniaProperty.Register<HexSurface, IBrush?>(nameof(OtherByteForeground));

    private readonly Dictionary<long, TextLayout> layouts = new();
    private readonly List<long> leaving = new();
    private readonly byte[] rowBytes = new byte[HexRows.BytesPerRow];
    private IHexDocument? document;
    private int offsetDigits = HexRows.MinOffsetDigits;
    private char[] line = new char[HexRows.LineLength(HexRows.MinOffsetDigits)];
    private double scrollTop;

    // Run properties per byte class, built for the current font and brushes and dropped with
    // the layouts when either changes.
    private TextRunProperties? mutedRun;
    private TextRunProperties? otherRun;

    static HexSurface()
    {
        AffectsRender<HexSurface>(OtherByteForegroundProperty);
    }

    /// <summary>Brush for bytes that are neither zero nor printable ASCII.</summary>
    public IBrush? OtherByteForeground
    {
        get => GetValue(OtherByteForegroundProperty);
        set => SetValue(OtherByteForegroundProperty, value);
    }

    /// <summary>The bytes drawn. Setting it starts at the top, sizes the offset column for the
    /// document's length, and sizes the pan range from one full row.</summary>
    public IHexDocument? Document
    {
        get => this.document;
        set
        {
            this.document = value;
            this.offsetDigits = HexRows.OffsetDigits(value?.Length ?? 0);
            this.line = new char[HexRows.LineLength(this.offsetDigits)];
            this.scrollTop = 0;
            DropLayouts();
            ResetWidestRowWidth(FullRowWidth());
            InvalidateVisual();
            NotifyScrollPosition();
        }
    }

    /// <summary>The first row on screen, for tests and the status line.</summary>
    public long TopRow => (long)(this.scrollTop / RowHeight);

    /// <summary>Row <paramref name="row"/> laid out as it is drawn, for tests.</summary>
    internal TextLayout LaidOutRow(long row) => LayoutFor(row, new Typeface(FontFamily));

    /// <summary>Layouts held, for tests: a screen's worth at most.</summary>
    internal int CachedLayoutCount => this.layouts.Count;

    private long RowCount => HexRows.RowCount(this.document?.Length ?? 0);

    // Live, never cached: a growing document's extent must not be clamped against a stale one.
    private double ExtentHeight => RowCount * RowHeight;

    private double MaxScrollTop => Math.Max(0, ExtentHeight - Bounds.Height);

    public override double ScrollFraction => ExtentHeight <= 0 ? 0 : this.scrollTop / ExtentHeight;

    public override double ViewportFraction => ExtentHeight <= 0 ? 1 : Math.Min(1, Bounds.Height / ExtentHeight);

    public override bool ShowsEnd => this.scrollTop >= MaxScrollTop - 0.5;

    public override void ScrollByPixels(double delta) => SetVerticalOffset(this.scrollTop + delta);

    public override void ScrollToFraction(double fraction) => SetVerticalOffset(Math.Clamp(fraction, 0, 1) * ExtentHeight);

    public override void ScrollToEnd() => SetVerticalOffset(MaxScrollTop);

    /// <summary>Puts row <paramref name="row"/> at the top, or as near as the end allows.</summary>
    public void ScrollToRow(long row) => SetVerticalOffset(row * RowHeight);

    private void SetVerticalOffset(double y)
    {
        double clamped = Math.Clamp(y, 0, MaxScrollTop);
        if (clamped != this.scrollTop)
        {
            this.scrollTop = clamped;
            InvalidateVisual();
        }

        NotifyScrollPosition();
    }

    protected override void OnViewportChanged() => SetVerticalOffset(this.scrollTop);

    protected override void OnTextStyleChanged()
    {
        DropLayouts();
        ResetWidestRowWidth(FullRowWidth());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A layout bakes its brushes in, so a theme change re-lays the rows on screen.
        if (change.Property == ForegroundProperty || change.Property == GutterForegroundProperty
            || change.Property == OtherByteForegroundProperty)
        {
            DropLayouts();
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool toEnds = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        double page = Math.Max(RowHeight, Bounds.Height - RowHeight);
        switch (e.Key)
        {
            case Key.Up: ScrollByPixels(-RowHeight); break;
            case Key.Down: ScrollByPixels(RowHeight); break;
            case Key.PageUp: ScrollByPixels(-page); break;
            case Key.PageDown: ScrollByPixels(page); break;
            case Key.Home when toEnds: SetVerticalOffset(0); break;
            case Key.End when toEnds: ScrollToEnd(); break;
            default: return;
        }

        e.Handled = true;
    }

    protected override void RenderRows(DrawingContext context)
    {
        if (this.document is null || RowCount == 0)
            return;

        long first = (long)(this.scrollTop / RowHeight);
        long last = Math.Min(RowCount - 1, (long)((this.scrollTop + Bounds.Height) / RowHeight));
        DropLayoutsOutside(first, last);

        var typeface = new Typeface(FontFamily);
        double x = ContentPaddingX - PanOffset;
        for (long row = first; row <= last; row++)
        {
            var layout = LayoutFor(row, typeface);
            double top = row * RowHeight - this.scrollTop;
            layout.Draw(context, new Point(x, CentreInRow(layout, top)));
        }
    }

    private TextLayout LayoutFor(long row, Typeface typeface)
    {
        if (this.layouts.TryGetValue(row, out var cached))
            return cached;

        int count = this.document!.ReadRow(row, this.rowBytes);
        int length = HexRows.Format(this.rowBytes.AsSpan(0, count), HexRows.StartOf(row), this.offsetDigits, this.line);

        var layout = new TextLayout(new string(this.line, 0, length), typeface, FontSize, Foreground ?? Brushes.Black,
            TextAlignment.Left, TextWrapping.NoWrap, textStyleOverrides: StylesFor(count, typeface));
        this.layouts[row] = layout;
        return layout;
    }

    /// <summary>The coloured spans of a row of <paramref name="count"/> bytes: the offset muted,
    /// and each byte that is not printable ASCII - its hex digits and its character - in its
    /// class's colour. Printable bytes take the layout's own foreground.</summary>
    private List<ValueSpan<TextRunProperties>> StylesFor(int count, Typeface typeface)
    {
        this.mutedRun ??= Run(typeface, GutterForeground);
        this.otherRun ??= Run(typeface, OtherByteForeground);

        // A layout keeps the list it is given, so each row has its own.
        var styles = new List<ValueSpan<TextRunProperties>>(1 + 2 * count)
        {
            new(0, this.offsetDigits, this.mutedRun),
        };

        // In order of position, which a layout requires: every hex span, then every character.
        for (int i = 0; i < count; i++)
        {
            if (RunFor(this.rowBytes[i]) is { } run)
                styles.Add(new ValueSpan<TextRunProperties>(HexRows.HexPosition(this.offsetDigits, i), 2, run));
        }

        int characters = HexRows.CharacterColumn(this.offsetDigits);
        for (int i = 0; i < count; i++)
        {
            if (RunFor(this.rowBytes[i]) is { } run)
                styles.Add(new ValueSpan<TextRunProperties>(characters + i, 1, run));
        }

        return styles;
    }

    private TextRunProperties? RunFor(byte value) => HexRows.ClassOf(value) switch
    {
        HexByteClass.Zero => this.mutedRun,
        HexByteClass.Other => this.otherRun,
        _ => null,
    };

    private TextRunProperties Run(Typeface typeface, IBrush? brush)
        => new GenericTextRunProperties(typeface, FontSize, foregroundBrush: brush ?? Foreground ?? Brushes.Black);

    /// <summary>How wide a full row is in the current font - every row's width, since the font is
    /// monospaced and a row's text is fixed-width. Measured when the document or font changes,
    /// never while drawing.</summary>
    private double FullRowWidth()
    {
        using var sample = new TextLayout(new string('0', HexRows.LineLength(this.offsetDigits)), new Typeface(FontFamily), FontSize, Brushes.Black);
        return sample.WidthIncludingTrailingWhitespace;
    }

    private void DropLayoutsOutside(long first, long last)
    {
        foreach (long row in this.layouts.Keys)
        {
            if (row < first || row > last)
                this.leaving.Add(row);
        }

        foreach (long row in this.leaving)
        {
            this.layouts[row].Dispose();
            this.layouts.Remove(row);
        }

        this.leaving.Clear();
    }

    private void DropLayouts()
    {
        foreach (var layout in this.layouts.Values)
            layout.Dispose();
        this.layouts.Clear();
        this.mutedRun = null;
        this.otherRun = null;
    }
}
