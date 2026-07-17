using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace JsonViewerCore.Shell;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);

    public static Task<bool> Show(Window owner, string message, string confirmText = "Replace")
    {
        var dialog = new ConfirmDialog();
        dialog.MessageText.Text = message;
        dialog.YesButton.Content = confirmText;
        return dialog.ShowDialog<bool>(owner);
    }
}
