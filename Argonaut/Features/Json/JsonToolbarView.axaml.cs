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
    /// Dismisses the schema-type flyout once a type has actually been picked. The view model
    /// ignores header rows, so this has to as well - clicking a section heading must not look
    /// like a choice and close the picker. Also clears the filter, so reopening starts from the
    /// whole list rather than from whatever was last typed.
    /// </summary>
    private void OnSchemaRootPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not SchemaRootPick { IsSelectable: true })
            return;

        if (DataContext is JsonToolbarViewModel vm)
            vm.SchemaRootPicker.Filter = string.Empty;

        SchemaRootButton.Flyout?.Hide();
    }
}
