using Avalonia.Controls;

namespace Argonaut.Features.Json.ArrayTable;

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
