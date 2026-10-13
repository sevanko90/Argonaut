using System.Threading.Tasks;
using Argonaut.Shell.CommandLine;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell.Dialogs;

/// <summary>
/// The command-line reference - <see cref="CommandLineUsage.Text"/>, exactly what <c>--help</c>
/// prints - for whoever cannot see a terminal: reached from the About box, and opened by
/// <c>--help</c> itself when the app was started without one.
/// </summary>
public partial class CommandLineDialog : Window
{
    public CommandLineDialog()
    {
        InitializeComponent();
        UsageText.Text = CommandLineUsage.Text;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public static Task ShowUsage(Window owner) => new CommandLineDialog().ShowDialog(owner);
}
