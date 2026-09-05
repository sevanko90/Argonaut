using Avalonia.Controls;

namespace Argonaut.Features.Json;

public partial class JsonArrayTableToolbarView : UserControl
{
    public JsonArrayTableToolbarView()
    {
        InitializeComponent();
    }

    private void OnBack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is JsonArrayTableToolbarViewModel vm)
            _ = vm.BackAsync();
    }
}
