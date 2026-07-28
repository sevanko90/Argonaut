using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell;

public partial class IncompatibleView : UserControl
{
    public IncompatibleView()
    {
        InitializeComponent();
    }

    private void OnOpenAsRawText(object? sender, RoutedEventArgs e)
    {
        (DataContext as IncompatibleViewModel)?.OpenAsRawText();
    }

    private void OnJumpToFailureLocation(object? sender, RoutedEventArgs e)
    {
        (DataContext as IncompatibleViewModel)?.JumpToFailureLocation();
    }
}
