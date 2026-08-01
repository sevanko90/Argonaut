using Argonaut.Infrastructure;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Argonaut.Shell;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        NameText.Text = AppInfo.Name;
        VersionText.Text = $"Version {AppInfo.Version}";
        RepoLinkButton.Content = AppInfo.RepoUrl;
        AutoUpdateCheckBox.IsChecked = AutoUpdatePreference.Load();
    }

    private void OnAutoUpdateToggled(object? sender, RoutedEventArgs e) =>
        AutoUpdatePreference.Save(AutoUpdateCheckBox.IsChecked ?? true);

    private void OnRepoLinkClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - opening the browser must never crash the dialog.
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public static Task ShowAbout(Window owner) => new AboutDialog().ShowDialog(owner);
}
