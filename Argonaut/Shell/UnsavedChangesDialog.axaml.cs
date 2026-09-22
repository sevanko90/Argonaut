using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell;

/// <summary>Save / Don't Save / Cancel, asked before anything that would drop unsaved edits.</summary>
public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Cancel);

    public static Task<UnsavedChangesChoice> Show(Window owner, string message)
    {
        var dialog = new UnsavedChangesDialog();
        dialog.MessageText.Text = message;
        return dialog.ShowDialog<UnsavedChangesChoice>(owner);
    }
}
