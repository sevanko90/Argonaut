using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Features.Hex;

/// <summary>The hex view's toolbar - see <see cref="HexToolbarViewModel"/>.</summary>
public partial class HexToolbarView : UserControl
{
    public HexToolbarView()
    {
        InitializeComponent();
    }

    private async void OnSaveCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HexToolbarViewModel toolbar)
            await toolbar.SaveCopyAsync();
    }

    private async void OnOpenInDefaultApp(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HexToolbarViewModel toolbar)
            await toolbar.OpenInDefaultAppAsync();
    }
}
