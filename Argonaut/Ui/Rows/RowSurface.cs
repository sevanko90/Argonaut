using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Ui.Rows;

/// <summary>
/// The base of every view that draws its own rows rather than templating a control per row: the
/// raw text view, and the tree views. It owns what they share - fixed-height rows, the appearance
/// properties, the background that makes the surface hit-testable, horizontal pan, the wheel, and
/// one scroll interface that <see cref="RowScrollBars"/> drives the same way for every surface.
/// What a row is, how many there are and where the view is among them is the derived surface's.
///
/// <b>Vertical position is a fraction of the document</b> (<see cref="ScrollFraction"/>), and
/// the host's scrollbar only drives it and follows it - no surface sits in a
/// <c>ScrollViewer</c>, because sharing one offset with a host and re-syncing it after every
/// scroll made a dragged thumb stutter and scrolling up from the end snap back. A surface
/// supplies one of two models behind the interface: <b>exact</b>, for one that knows its row
/// count (the raw view: the fraction is the offset over rows x height, so the thumb is
/// row-accurate), or <b>estimated</b>, for one that does not (the trees: the fraction is the top
/// row's byte position in the document).
///
/// Horizontal movement is deliberately not scrolling: rows are laid out at their natural width and
/// panned by <see cref="PanOffset"/>, which keeps gutters pinned while the text slides under them.
/// </summary>
public abstract class RowSurface : Control
{
    /// <summary>Row height, in device-independent pixels. Every row is exactly this tall, which is
    /// what makes the visible range arithmetic on the scroll offset.</summary>
    public const double RowHeight = 22;

    /// <summary>Padding inside the surface, matching the ListBox padding the surfaces replace.</summary>
    public const double ContentPaddingX = 8;

    private double panOffset;
    private double widestRowWidth;

    static RowSurface()
    {
        FocusableProperty.OverrideDefaultValue<RowSurface>(true);

        // Transparent rather than null, so the surface is hit-testable even if the brush resource
        // it is bound to fails to resolve. A surface that silently stops responding to clicks
        // because of a missing theme key is not a failure anyone would look for here.
        BackgroundProperty.OverrideDefaultValue<RowSurface>(Brushes.Transparent);
        AffectsRender<RowSurface>(
            ForegroundProperty,
            GutterForegroundProperty,
            HighlightBrushProperty,
            SelectionBrushProperty,
            BackgroundProperty);
    }

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<RowSurface>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<RowSurface>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RowSurface>();

    /// <summary>Brush for gutter text - line numbers, markers.</summary>
    public static readonly StyledProperty<IBrush?> GutterForegroundProperty =
        AvaloniaProperty.Register<RowSurface, IBrush?>(nameof(GutterForeground));

    /// <summary>Brush behind occurrences of the find term.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<RowSurface, IBrush?>(nameof(HighlightBrush));

    /// <summary>Brush behind what is selected.</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<RowSurface, IBrush?>(nameof(SelectionBrush));

    /// <summary>
    /// The surface's own background. Not decoration: a control with nothing painted behind it is
    /// not reliably hit-testable, so without this a click passes straight through.
    /// </summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<RowSurface>();

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? GutterForeground
    {
        get => GetValue(GutterForegroundProperty);
        set => SetValue(GutterForegroundProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>
    /// How far the text is panned left, in pixels. Not scrolling: the gutters stay put and only
    /// the text column moves, which is what keeps a gutter aligned with its row at any horizontal
    /// position.
    /// </summary>
    public double PanOffset
    {
        get => this.panOffset;
        set
        {
            if (Math.Abs(this.panOffset - value) < 0.01)
                return;

            this.panOffset = value;
            InvalidateVisual();
        }
    }

    /// <summary>Asks the host to pan so a given surface x is visible. The pan scrollbar belongs
    /// to the view, so the surface requests rather than sets.</summary>
    public event EventHandler<double>? PanRequested;

    /// <summary>
    /// How wide the text column has actually needed to be, in pixels: the widest row measured
    /// since <see cref="ResetWidestRowWidth"/>. This is what the pan range is sized from.
    ///
    /// It is a high-water mark rather than the widest row on screen, because a range measured
    /// from the current viewport would shrink and grow as the user scrolled, moving the thumb
    /// under their hand. A derived surface resets it exactly when the old measurement stops
    /// meaning anything - a new document, a new font.
    /// </summary>
    public double WidestRowWidth => this.widestRowWidth;

    /// <summary>Raised when <see cref="WidestRowWidth"/> changes, so the host can resize the pan
    /// range.</summary>
    public event EventHandler? WidestRowWidthChanged;

    protected void RequestPan(double x) => PanRequested?.Invoke(this, x);

    /// <summary>Raises the high-water mark to <paramref name="width"/> if it is wider.</summary>
    protected void RecordRowWidth(double width)
    {
        if (width <= this.widestRowWidth)
            return;

        this.widestRowWidth = width;
        NotifyWidestRowWidthChanged();
    }

    protected void ResetWidestRowWidth()
    {
        if (this.widestRowWidth == 0)
            return;

        this.widestRowWidth = 0;
        NotifyWidestRowWidthChanged();
    }

    /// <summary>
    /// Raises <see cref="WidestRowWidthChanged"/> a dispatcher turn later. One of the call sites
    /// is the render pass, and the host reacts by resizing the pan scrollbar - a visual change,
    /// which Avalonia refuses mid-render ("Visual was invalidated during the render pass"). The
    /// deferral is the same re-entrancy tool the selection setters use (see CLAUDE.md), for the
    /// same reason: decide synchronously, act after the pass that asked has unwound.
    /// </summary>
    private void NotifyWidestRowWidthChanged()
    {
        if (WidestRowWidthChanged is null)
            return;

        UiDeferral.AfterCurrentInput(() => WidestRowWidthChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Top of a line of text within its row band. Rows are a fixed height while the text is however
    /// tall the content font makes it, so the difference is split above and below rather than left
    /// at the bottom.
    /// </summary>
    protected static double CentreInRow(TextLayout layout, double rowTop)
        => rowTop + Math.Max(0, (RowHeight - layout.Height) / 2);

    /// <summary>The font or its size changed: anything laid out is stale.</summary>
    protected virtual void OnTextStyleChanged()
    {
    }

    /// <summary>The surface was resized: the rows it covers, and the scroll extent, may have
    /// changed.</summary>
    protected virtual void OnViewportChanged()
    {
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            OnTextStyleChanged();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            OnViewportChanged();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Background is { } background)
            context.FillRectangle(background, new Rect(Bounds.Size));
    }

    // ---- scrolling ------------------------------------------------------------------------

    /// <summary>Raised whenever the view moves or the document under it grows or shrinks - a
    /// scroll, a reveal, a keyboard move, a scan's progress - so a scrollbar can follow.</summary>
    public event EventHandler? ScrollPositionChanged;

    protected void NotifyScrollPosition() => ScrollPositionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Where the top of the view is in the document, from 0 to 1.</summary>
    public abstract double ScrollFraction { get; }

    /// <summary>How much of the document a screen shows, from 0 to 1; 1 when it all fits.</summary>
    public abstract double ViewportFraction { get; }

    /// <summary>True when the document's last row is on screen in full - the view is at the end,
    /// whatever an estimated fraction says.</summary>
    public abstract bool ShowsEnd { get; }

    /// <summary>Moves the view by <paramref name="delta"/> pixels of rows - the wheel, a trackpad,
    /// a scrollbar's arrows and pages. Positive moves down the document.</summary>
    public abstract void ScrollByPixels(double delta);

    /// <summary>Puts <paramref name="fraction"/> of the document at the top - what a dragged thumb
    /// does.</summary>
    public abstract void ScrollToFraction(double fraction);

    /// <summary>Shows the end of the document, its last row at the bottom.</summary>
    public abstract void ScrollToEnd();

    /// <summary>How wide the text column is - what the pan range is measured against.</summary>
    public virtual double PanViewportWidth => Math.Max(0, Bounds.Width - 2 * ContentPaddingX);

    /// <summary>One click of the pan scrollbar's arrows, in pixels.</summary>
    public virtual double PanStep => RowHeight;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // One row per unit of wheel delta; a trackpad reports fractions of that, which is what
        // makes it smooth.
        if (e.Delta.Y != 0)
            ScrollByPixels(-e.Delta.Y * RowHeight);
        if (e.Delta.X != 0)
            RequestPan(Math.Max(0, PanOffset - e.Delta.X * RowHeight));

        e.Handled = true;
    }
}
