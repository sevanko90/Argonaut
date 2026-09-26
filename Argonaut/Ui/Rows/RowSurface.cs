using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Ui.Rows;

/// <summary>
/// The base of every view that draws its own rows rather than templating a control per row: the
/// raw text view, and the tree views. It owns what they share - fixed-height rows, the appearance
/// properties, the background that makes the surface hit-testable, horizontal pan, and the
/// <see cref="ILogicalScrollable"/> plumbing that lets a hosting <c>ScrollViewer</c> supply the
/// wheel, the scrollbar and page keys. What a row is, how many there are and how the scroll offset
/// maps onto them is the derived surface's.
///
/// Horizontal movement is deliberately not scrolling: rows are laid out at their natural width and
/// panned by <see cref="PanOffset"/>, which keeps gutters pinned while the text slides under them.
/// </summary>
public abstract class RowSurface : Control, ILogicalScrollable
{
    /// <summary>Row height, in device-independent pixels. Every row is exactly this tall, which is
    /// what makes the visible range arithmetic on the scroll offset.</summary>
    public const double RowHeight = 22;

    /// <summary>Padding inside the surface, matching the ListBox padding the surfaces replace.</summary>
    public const double ContentPaddingX = 8;

    private Vector offset;
    private double panOffset;
    private double widestRowWidth;
    private EventHandler? scrollInvalidated;

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

    // ---- ILogicalScrollable -------------------------------------------------------------
    //
    // Vertical only. Horizontal movement is PanOffset, driven by the view's own pan scrollbar,
    // because scrolling horizontally would take the gutters with it.

    /// <summary>How tall the rows are altogether, in pixels. Read live by <see cref="Extent"/>;
    /// a derived surface computes it rather than caching it, so the host never clamps an offset
    /// against a stale value.</summary>
    protected abstract double ExtentHeight { get; }

    /// <summary>The vertical offset moved: realize the rows it now covers.</summary>
    protected abstract void OnOffsetChanged();

    public Size Extent => new(Bounds.Width, Math.Max(ExtentHeight, Bounds.Height));

    public Size Viewport => Bounds.Size;

    public Vector Offset
    {
        get => this.offset;
        set
        {
            if (this.offset == value)
                return;

            this.offset = value;
            OnOffsetChanged();
            InvalidateVisual();
        }
    }

    public bool CanHorizontallyScroll
    {
        get => false;
        set { }
    }

    public bool CanVerticallyScroll
    {
        get => true;
        set { }
    }

    public bool IsLogicalScrollEnabled => true;

    /// <summary>One wheel notch, and the arrow-key step the ScrollViewer applies.</summary>
    public Size ScrollSize => new(1, RowHeight);

    public Size PageScrollSize => new(Viewport.Width, Math.Max(RowHeight, Viewport.Height - RowHeight));

    public event EventHandler? ScrollInvalidated
    {
        add => this.scrollInvalidated += value;
        remove => this.scrollInvalidated -= value;
    }

    public void RaiseScrollInvalidated(EventArgs e) => this.scrollInvalidated?.Invoke(this, e);

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    /// <summary>Moves the vertical offset to <paramref name="y"/>, clamped to the extent, and
    /// tells the host.</summary>
    protected void SetVerticalOffset(double y)
    {
        double limit = Math.Max(0, Extent.Height - Bounds.Height);
        Offset = new Vector(this.offset.X, Math.Clamp(y, 0, limit));
        RaiseScrollInvalidated(EventArgs.Empty);
    }
}
