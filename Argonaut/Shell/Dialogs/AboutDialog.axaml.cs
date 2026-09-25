using Argonaut.Shell.Updates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Argonaut.Shell.Dialogs;

public partial class AboutDialog : Window
{
    // Set by ShowAbout before the dialog is shown. Not a constructor parameter, because the XAML
    // runtime loader needs a parameterless one.
    private UpdateSettings? updateSettings;

    public AboutDialog()
    {
        InitializeComponent();

        NameText.Text = AppInfo.Name;
        VersionText.Text = $"Version {AppInfo.Version}";
        RepoLinkButton.Content = AppInfo.RepoUrl;
    }

    private void OnAutoUpdateToggled(object? sender, RoutedEventArgs e)
    {
        if (updateSettings is not null)
            updateSettings.CheckOnStartup = AutoUpdateCheckBox.IsChecked ?? true;
    }

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

    private async void OnNoticesLinkClicked(object? sender, RoutedEventArgs e)
    {
        await NoticesDialog.ShowNotices(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public static Task ShowAbout(Window owner, UpdateSettings updateSettings)
    {
        var dialog = new AboutDialog();
        dialog.AutoUpdateCheckBox.IsChecked = updateSettings.CheckOnStartup;
        dialog.updateSettings = updateSettings;
        return dialog.ShowDialog(owner);
    }
}
