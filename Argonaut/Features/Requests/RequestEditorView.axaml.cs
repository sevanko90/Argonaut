using Argonaut.Ui.Icons;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Argonaut.Features.Requests;

/// <summary>The request pane - see <see cref="RequestEditorViewModel"/>. Enter in the URL sends.</summary>
public partial class RequestEditorView : UserControl
{
    public RequestEditorView()
    {
        InitializeComponent();
    }

    private RequestEditorViewModel? Editor => DataContext as RequestEditorViewModel;

    private void OnSend(object? sender, RoutedEventArgs e) => _ = Editor?.SendAsync();

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Editor is not { } editor)
            return;

        e.Handled = true;
        _ = editor.SendAsync();
    }

    private void OnAddArgument(object? sender, RoutedEventArgs e) => Editor?.AddArgument();

    private void OnRemoveArgument(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is QueryArgumentRowViewModel row)
            Editor?.RemoveArgument(row);
    }

    private void OnToggleCollapsed(object? sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor)
            return;

        editor.IsCollapsed = !editor.IsCollapsed;
        if (this.FindControl<LucideIcon>("FoldIcon") is { } icon && Application.Current is { } app
            && app.TryGetResource(editor.IsCollapsed ? "Icon.ChevronDown" : "Icon.ChevronUp", ActualThemeVariant, out var geometry))
            icon.Data = (Avalonia.Media.Geometry?)geometry;
    }
}
