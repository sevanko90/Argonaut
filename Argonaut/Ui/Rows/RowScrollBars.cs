using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Argonaut.Ui.Rows;

/// <summary>
/// Drives a <see cref="RowSurface"/> from a view's scrollbars, the same way for every surface: a
/// vertical bar over the surface's position in the document, and a pan bar over the width of its
/// rows. The view lays the bars out; this keeps them and the surface in step.
///
/// <b>The vertical bar only drives and follows.</b> Its range is the document as fractions and
/// its thumb a screen's share. A dragged thumb puts that fraction at the top (the bottom of the
/// track, the end); the arrows and the track move by a row and a page, as the wheel does. While
/// the thumb is held the bar is not told where the view went - it is the one moving it, and
/// feeding the surface's answer back is what made a dragged thumb stutter.
///
/// <b>The pan bar</b> is sized from the widest row the surface has laid out - a high-water mark,
/// so the range never shrinks under the user mid-scroll - against the width the rows have. It
/// owns the pan offset; the surface asks it to move (<see cref="RowSurface.PanRequested"/>).
///
/// UI-thread only. <see cref="Dispose"/> unhooks everything.
/// </summary>
public sealed class RowScrollBars : IDisposable
{
    private readonly RowSurface surface;
    private readonly ScrollBar vertical;
    private readonly ScrollBar? pan;

    /// <summary>True while the vertical thumb is held.</summary>
    private bool draggingThumb;

    public RowScrollBars(RowSurface surface, ScrollBar vertical, ScrollBar? pan)
    {
        this.surface = surface;
        this.vertical = vertical;
        this.pan = pan;

        surface.ScrollPositionChanged += OnScrollPositionChanged;
        surface.SizeChanged += OnSurfaceSizeChanged;
        surface.WidestRowWidthChanged += OnWidestRowWidthChanged;
        surface.PanRequested += OnPanRequested;
        vertical.Scroll += OnVerticalScroll;
        if (pan is not null)
            pan.ValueChanged += OnPanValueChanged;

        Refresh();
    }

    /// <summary>Re-reads both ranges - after a new document, a new font, anything the surface's
    /// own events do not announce.</summary>
    public void Refresh()
    {
        UpdateVertical();
        UpdatePan();
    }

    /// <summary>Back to the left edge, for rows that have just been replaced wholesale.</summary>
    public void ResetPan()
    {
        if (pan is not null)
            pan.Value = 0;
        surface.PanOffset = 0;
    }

    public void Dispose()
    {
        surface.ScrollPositionChanged -= OnScrollPositionChanged;
        surface.SizeChanged -= OnSurfaceSizeChanged;
        surface.WidestRowWidthChanged -= OnWidestRowWidthChanged;
        surface.PanRequested -= OnPanRequested;
        vertical.Scroll -= OnVerticalScroll;
        if (pan is not null)
            pan.ValueChanged -= OnPanValueChanged;
    }

    // ---- vertical -------------------------------------------------------------------------

    private void OnVerticalScroll(object? sender, ScrollEventArgs e) => OnVerticalScroll(e.ScrollEventType, e.NewValue);

    /// <summary>The user worked the vertical bar. Internal so a test can work it too.</summary>
    internal void OnVerticalScroll(ScrollEventType type, double newValue)
    {
        double page = Math.Max(RowSurface.RowHeight, surface.Bounds.Height - RowSurface.RowHeight);
        switch (type)
        {
            case ScrollEventType.ThumbTrack:
                draggingThumb = true;
                // At the bottom of the track, the end - not wherever an estimate lands.
                if (newValue >= vertical.Maximum)
                    surface.ScrollToEnd();
                else
                    surface.ScrollToFraction(newValue);
                break;
            case ScrollEventType.EndScroll:
                draggingThumb = false;
                UpdateVertical();
                break;
            case ScrollEventType.SmallIncrement:
                surface.ScrollByPixels(RowSurface.RowHeight);
                break;
            case ScrollEventType.SmallDecrement:
                surface.ScrollByPixels(-RowSurface.RowHeight);
                break;
            case ScrollEventType.LargeIncrement:
                surface.ScrollByPixels(page);
                break;
            case ScrollEventType.LargeDecrement:
                surface.ScrollByPixels(-page);
                break;
        }
    }

    private void OnScrollPositionChanged(object? sender, EventArgs e)
    {
        if (!draggingThumb)
            UpdateVertical();
    }

    /// <summary>Hidden when a screen shows everything.</summary>
    private void UpdateVertical()
    {
        double viewport = surface.ViewportFraction;
        if (viewport >= 1 || surface.Bounds.Height <= 0)
        {
            vertical.IsVisible = false;
            return;
        }

        double maximum = 1 - viewport;
        vertical.Maximum = maximum;
        vertical.ViewportSize = viewport;
        vertical.LargeChange = viewport;
        vertical.SmallChange = viewport / Math.Max(1, surface.Bounds.Height / RowSurface.RowHeight);
        vertical.Value = surface.ShowsEnd ? maximum : Math.Min(surface.ScrollFraction, maximum);
        vertical.IsVisible = true;
    }

    // ---- pan ------------------------------------------------------------------------------

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => Refresh();

    private void OnWidestRowWidthChanged(object? sender, EventArgs e) => UpdatePan();

    private void OnPanValueChanged(object? sender, RangeBaseValueChangedEventArgs e) => surface.PanOffset = e.NewValue;

    private void OnPanRequested(object? sender, double desiredOffset)
    {
        if (pan is { IsVisible: true })
            pan.Value = Math.Clamp(desiredOffset, pan.Minimum, pan.Maximum);
    }

    private void UpdatePan()
    {
        if (pan is null)
            return;

        double viewport = surface.PanViewportWidth;
        double maximum = Math.Max(0, surface.WidestRowWidth - viewport);
        if (viewport <= 0 || maximum <= 0)
        {
            pan.IsVisible = false;
            ResetPan();
            return;
        }

        pan.Maximum = maximum;
        pan.ViewportSize = viewport;
        pan.LargeChange = viewport;
        pan.SmallChange = surface.PanStep;
        if (pan.Value > maximum)
            pan.Value = maximum;
        pan.IsVisible = true;
    }
}
