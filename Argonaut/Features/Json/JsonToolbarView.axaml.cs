using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Argonaut.Features.Json;

public partial class JsonToolbarView : UserControl
{
    public JsonToolbarView()
    {
        InitializeComponent();
    }

    private async void OnGoToPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JsonToolbarViewModel vm)
            await vm.GoToPathAsync();
    }

    private async void OnJsonPathTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is JsonToolbarViewModel vm)
            await vm.GoToPathAsync();
    }

    /// <summary>
    /// Dismisses the schema flyout once a type has actually been picked. The view model ignores
    /// header rows, so this has to as well - clicking a section heading must not look like a
    /// choice and close the picker. Also clears the filter, so reopening starts from the whole
    /// list rather than from whatever was last typed.
    /// </summary>
    private void OnSchemaRootPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not SchemaRootPick { IsSelectable: true })
            return;

        CloseSchemaFlyout();
    }

    /// <summary>
    /// Dismisses the flyout once a schema file has been picked. Selecting a schema rebinds a
    /// root automatically, so closing is right for the common case; changing the type as well
    /// means reopening, which is one click on a control that is now a single button.
    ///
    /// Ignores the selection the list makes on its own as the item list is rebuilt - only a user
    /// pick carries an added item.
    /// </summary>
    private void OnSchemaPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0)
            return;

        CloseSchemaFlyout();
    }

    private void CloseSchemaFlyout()
    {
        if (DataContext is JsonToolbarViewModel vm)
            vm.SchemaRootPicker.Filter = string.Empty;

        SchemaButton.Flyout?.Hide();
    }
}
