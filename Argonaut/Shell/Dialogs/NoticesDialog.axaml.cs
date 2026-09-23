using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Argonaut.Shell.Dialogs;

/// <summary>
/// Argonaut's licence and the notices of every component it ships, one component at a time. The
/// notices are read from the embedded resources when the window opens and released with it.
/// </summary>
public partial class NoticesDialog : Window
{
    public NoticesDialog()
    {
        InitializeComponent();

        ComponentListBox.ItemsSource = LicenseNotices.Load();
        ComponentListBox.SelectedIndex = 0;
    }

    private void OnComponentSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ComponentListBox.SelectedItem is not LicenseNotice notice)
            return;

        // Only this window's own detail controls change - never the list's ItemsSource - so acting
        // inside the selection commit is safe (see UiDeferral for the case where it is not).
        ComponentText.Text = notice.Component;
        HomepageLinkButton.Content = notice.Homepage;
        HomepageLinkButton.IsVisible = notice.Homepage is not null;
        PackagesText.Text = notice.Packages;
        PackagesText.IsVisible = notice.Packages is not null;
        NoticeText.Text = notice.NoticeText;
        NoticeScrollViewer.Offset = default(Vector);
    }

    private void OnHomepageLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (HomepageLinkButton.Content is not string homepage)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(homepage) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - opening the browser must never crash the dialog.
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public static Task ShowNotices(Window owner) => new NoticesDialog().ShowDialog(owner);
}
