using Avalonia.Controls;

namespace Argonaut.Features.Json.Diff;

public partial class JsonDiffToolbarView : UserControl
{
    public JsonDiffToolbarView()
    {
        InitializeComponent();
    }

    private void OnPreviousDiff(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is JsonDiffToolbarViewModel vm)
            vm.GoToPreviousDiff();
    }

    private void OnNextDiff(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is JsonDiffToolbarViewModel vm)
            vm.GoToNextDiff();
    }
}
