using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Features.Raw.Recipes;

/// <summary>
/// Edits, previews and saves recipes over a <see cref="RecipeEditorViewModel"/>. Closes with
/// true when the user chose to apply; applying is the caller's, so the document changes only
/// after the dialog has gone.
/// </summary>
public partial class RecipeEditorDialog : Window
{
    public RecipeEditorDialog()
    {
        InitializeComponent();
    }

    private RecipeEditorViewModel Editor => (RecipeEditorViewModel)DataContext!;

    private void OnSave(object? sender, RoutedEventArgs e) => Editor.Save();

    private void OnDelete(object? sender, RoutedEventArgs e) => Editor.Delete();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnApply(object? sender, RoutedEventArgs e) => Close(true);

    /// <summary>Shows the dialog over <paramref name="owner"/>; true when the user chose to apply.</summary>
    public static Task<bool> ShowAsync(Window owner, RecipeEditorViewModel editor) =>
        new RecipeEditorDialog { DataContext = editor }.ShowDialog<bool>(owner);
}
