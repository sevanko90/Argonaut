using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace Argonaut.Shell;

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

    /// <summary>A message with a single acknowledgement - for a failure the user has to read,
    /// which a toast would take away before they had.</summary>
    public static Task Inform(Window owner, string message)
    {
        var dialog = new ConfirmDialog();
        dialog.MessageText.Text = message;
        dialog.YesButton.Content = "OK";
        dialog.NoButton.IsVisible = false;
        return dialog.ShowDialog<bool>(owner);
    }
}
