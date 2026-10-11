using System;
using System.IO;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Rows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Argonaut.Features.Json.Preview;

/// <summary>Shows a <see cref="ValuePreview"/> - its tree with its own scrollbars, its text, its
/// picture or its message.</summary>
public partial class ValuePreviewView : UserControl
{
    /// <summary>Whether the tree draws indent guides; the host passes on the remembered choice.</summary>
    public static readonly StyledProperty<bool> ShowIndentGuidesProperty =
        AvaloniaProperty.Register<ValuePreviewView, bool>(nameof(ShowIndentGuides));

    private RowScrollBars? scrollBars;
    private Bitmap? picture;

    public ValuePreviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowPreview(DataContext as ValuePreview);
    }

    // The scrollbars follow the surface only while the view is shown, so a card or pane that
    // closes leaves nothing subscribed behind it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        scrollBars ??= new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        scrollBars.Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        scrollBars?.Dispose();
        scrollBars = null;
        ReleasePicture();
    }

    public bool ShowIndentGuides
    {
        get => GetValue(ShowIndentGuidesProperty);
        set => SetValue(ShowIndentGuidesProperty, value);
    }

    /// <summary>The tree the preview draws, for a host that wants its selection or links.</summary>
    public Argonaut.Ui.Tree.TreeSurface TreeSurface => Surface;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowIndentGuidesProperty)
            Surface.ShowIndentGuides = ShowIndentGuides;
    }

    private void ShowPreview(ValuePreview? preview)
    {
        Surface.Document = preview?.Tree;
        JsonTreePalette.Apply(Surface, this);
        scrollBars?.ResetPan();
        scrollBars?.Refresh();

        ReleasePicture();
        if (preview?.Image is { } encoded)
        {
            try
            {
                using var stream = new MemoryStream(encoded, writable: false);
                picture = new Bitmap(stream);
            }
            catch (Exception)
            {
                // A signature can promise a picture the bytes do not hold; the view says so
                // rather than failing the host.
                picture = null;
            }
        }

        Picture.Source = picture;
        SizePicture(picture);
        PictureUnreadable.IsVisible = preview?.Image is not null && picture is null;
    }

    /// <summary>An icon this small or smaller is scaled up, by a whole step so each of its pixels
    /// stays square, to about <see cref="IconShownSize"/>.</summary>
    private const int IconMaxPixels = 64;

    private const int IconShownSize = 128;

    private void SizePicture(Bitmap? shown)
    {
        int largest = shown is null ? 0 : Math.Max(shown.PixelSize.Width, shown.PixelSize.Height);
        if (shown is not null && largest <= IconMaxPixels)
        {
            int scale = Math.Max(1, IconShownSize / largest);
            Picture.Width = shown.PixelSize.Width * scale;
            Picture.Height = shown.PixelSize.Height * scale;
            Picture.StretchDirection = Avalonia.Media.StretchDirection.Both;
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(Picture, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            return;
        }

        Picture.Width = double.NaN;
        Picture.Height = double.NaN;
        Picture.StretchDirection = Avalonia.Media.StretchDirection.DownOnly;
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(Picture, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    private void ReleasePicture()
    {
        Picture.Source = null;
        picture?.Dispose();
        picture = null;
    }
}
