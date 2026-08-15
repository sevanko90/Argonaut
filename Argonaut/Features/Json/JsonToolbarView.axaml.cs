using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Argonaut.Features.Json;

public partial class JsonToolbarView : UserControl
{
    public JsonToolbarView()
    {
        InitializeComponent();
    }

    private async void OnGoToPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JsonToolbarViewModel vm)
            await vm.GoToPathAsync();
    }

    private async void OnJsonPathTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is JsonToolbarViewModel vm)
            await vm.GoToPathAsync();
    }
}
