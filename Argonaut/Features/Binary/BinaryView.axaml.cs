using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Features.Binary;

/// <summary>A binary document, described - see <see cref="BinaryViewModel"/>.</summary>
public partial class BinaryView : UserControl
{
    public BinaryView()
    {
        InitializeComponent();
    }

    private async void OnSaveCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BinaryViewModel document)
            await document.SaveCopyAsync();
    }

    private async void OnOpenInDefaultApp(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BinaryViewModel document)
            await document.OpenInDefaultAppAsync();
    }
}
