using System;
using Argonaut.Engine.Hints;
using Argonaut.Ui.Documents.Navigation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Argonaut.Features.Json.Preview;

/// <summary>
/// The card a hint opens: what its value encodes, previewed, with a way to open it as a document
/// of its own. Hosted by each view whose hints open one, placed against the view's surface; it
/// owns the preview it shows and lets it go when it closes.
/// </summary>
public partial class HintCard : UserControl
{
    /// <summary>How tall the card's tree is: a screenful of a small document, a window onto a big one.</summary>
    private const double TreeHeight = 280;

    public static readonly StyledProperty<Control?> AnchorProperty =
        AvaloniaProperty.Register<HintCard, Control?>(nameof(Anchor));

    public static readonly StyledProperty<bool> ShowIndentGuidesProperty =
        AvaloniaProperty.Register<HintCard, bool>(nameof(ShowIndentGuides), true);

    private HintPreview? shown;

    public HintCard()
    {
        InitializeComponent();
    }

    /// <summary>The control the card is placed against; <see cref="Show"/>'s rectangle is in its coordinates.</summary>
    public Control? Anchor
    {
        get => GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }

    public bool ShowIndentGuides
    {
        get => GetValue(ShowIndentGuidesProperty);
        set => SetValue(ShowIndentGuidesProperty, value);
    }

    public bool IsOpen => Card.IsOpen;

    /// <summary>The preview on show, for tests; null once the card has let it go.</summary>
    internal ValuePreview? ShownPreview => Preview.DataContext as ValuePreview;

    /// <summary>Shows <paramref name="preview"/> by <paramref name="anchor"/>, taking it over: the
    /// card disposes of it when it closes or shows another.</summary>
    public void Show(HintPreview preview, Rect anchor)
    {
        Card.IsOpen = false;
        Release();
        shown = preview;

        Title.Text = preview.Preview.Title;
        OpenButton.IsVisible = preview.Decoded is { Kind: not ExpandedKind.Image };
        // A tree needs a height to lay its rows into; text and pictures take what they need.
        Preview.Height = preview.Preview.IsTree ? TreeHeight : double.NaN;
        Preview.MaxHeight = TreeHeight;
        Preview.DataContext = preview.Preview;

        Card.PlacementTarget = Anchor;
        Card.PlacementRect = anchor;
        Card.IsOpen = true;
    }

    public void Close() => Card.IsOpen = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowIndentGuidesProperty)
            Preview.ShowIndentGuides = ShowIndentGuides;
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (shown?.Decoded is { } decoded)
            OpenDocumentService.Request(new OpenDocumentRequest(decoded.Bytes, decoded.Title, "the decoded value"));
        Card.IsOpen = false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Card.IsOpen = false;
        e.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs e) => Release();

    /// <summary>Lets go of the preview - its tree reads bytes of its own that are released with it.</summary>
    private void Release()
    {
        Preview.DataContext = null;
        shown?.Preview.Dispose();
        shown = null;
    }
}
