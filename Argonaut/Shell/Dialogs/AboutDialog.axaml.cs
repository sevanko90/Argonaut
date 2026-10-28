using Argonaut.Ui.Launching;
using Argonaut.Shell.Updates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Threading.Tasks;

namespace Argonaut.Shell.Dialogs;

public partial class AboutDialog : Window
{
    // Set by ShowAbout before the dialog is shown. Not constructor parameters, because the XAML
    // runtime loader needs a parameterless one.
    private UpdateSettings? updateSettings;
    private ISystemLauncher? launcher;

    public AboutDialog()
    {
        InitializeComponent();

        NameText.Text = AppInfo.Name;
        VersionText.Text = $"Version {AppInfo.Version}";
        RepoLinkButton.Content = AppInfo.RepoUrl;
    }

    private async void OnCommandLineLinkClicked(object? sender, RoutedEventArgs e)
    {
        await CommandLineDialog.ShowUsage(this);
    }

    private void OnAutoUpdateToggled(object? sender, RoutedEventArgs e)
    {
        if (updateSettings is not null)
            updateSettings.CheckOnStartup = AutoUpdateCheckBox.IsChecked ?? true;
    }

    private void OnRepoLinkClicked(object? sender, RoutedEventArgs e)
        => launcher?.OpenAddress(new Uri(AppInfo.RepoUrl));

    private async void OnNoticesLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (launcher is not null)
            await NoticesDialog.ShowNotices(this, launcher);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public static Task ShowAbout(Window owner, UpdateSettings updateSettings, ISystemLauncher launcher)
    {
        var dialog = new AboutDialog();
        dialog.AutoUpdateCheckBox.IsChecked = updateSettings.CheckOnStartup;
        dialog.updateSettings = updateSettings;
        dialog.launcher = launcher;
        return dialog.ShowDialog(owner);
    }
}
