using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell.Dialogs;

/// <summary>
/// Asks what to do with a request that has unsaved changes as its pane closes. For a request
/// opened from the saved list: Update it, Save as New beside it, or leave it as it was. For one
/// never saved: Save, or not. Closing the window is Cancel.
/// </summary>
public partial class RequestSaveDialog : Window
{
    public RequestSaveDialog()
    {
        InitializeComponent();
    }

    private void OnUpdate(object? sender, RoutedEventArgs e) => Close(RequestSaveChoice.Update);

    private void OnSaveAsNew(object? sender, RoutedEventArgs e) => Close(RequestSaveChoice.SaveAsNew);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(RequestSaveChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(RequestSaveChoice.Cancel);

    public static async Task<RequestSaveChoice> Show(Window owner, RequestSaveQuestion question)
    {
        var dialog = new RequestSaveDialog();
        if (question.IsSaved)
        {
            dialog.MessageText.Text = $"Update the saved request \"{question.RequestName}\" with your changes?";
            dialog.DetailText.Text = "Save as New keeps it as it was and saves this version beside it.";
        }
        else
        {
            dialog.MessageText.Text = $"Save the request \"{question.RequestName}\"?";
            dialog.DetailText.Text = "It hasn't worked yet, so it isn't saved on its own. Save it to try again later.";
            dialog.UpdateButton.IsVisible = false;
            dialog.SaveAsNewButton.Content = "Save";
            dialog.SaveAsNewButton.Classes.Add("accent");
        }

        // A window closed without a choice is a Cancel.
        return await dialog.ShowDialog<RequestSaveChoice?>(owner) ?? RequestSaveChoice.Cancel;
    }
}
