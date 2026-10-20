using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Engine.Transforms;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Raw.Recipes;

/// <summary>
/// The raw toolbar's Transform menu: the built-in encodings, which apply at once, and the saved
/// recipes, which apply at once or open in the recipe dialog. Every item replaces the whole
/// document as one undoable edit (<see cref="RawViewModel.ApplyRecipeAsync"/>,
/// <see cref="RawViewModel.ApplyCodecAsync"/>) - undo is the preview for an encoding.
///
/// The menu's items are built when it opens rather than bound, so a recipe saved from the dialog
/// - or dropped into the folder by hand - is there the next time without anything watching.
/// </summary>
public sealed class RawTransformMenuViewModel : ObservableObject
{
    private readonly RawViewModel document;
    private readonly IRecipeStore? recipes;
    private bool eachLine;

    /// <param name="recipes">Where recipes are kept; null offers only the encodings.</param>
    public RawTransformMenuViewModel(RawViewModel document, IRecipeStore? recipes)
    {
        this.document = document;
        this.recipes = recipes;
        document.PropertyChanged += OnDocumentPropertyChanged;
    }

    /// <summary>Whatever lets the document be saved lets it be transformed: an editable
    /// document whose scan has finished, with no save or transform already reading it.</summary>
    public bool CanTransform => this.document.CanSave;

    /// <summary>Encode or decode each line on its own rather than the document as one text.</summary>
    public bool EachLine
    {
        get => this.eachLine;
        set => SetField(ref this.eachLine, value);
    }

    public bool HasRecipes => this.recipes is not null;

    public IReadOnlyList<RecipeEntry> ListRecipes() => this.recipes?.Enumerate() ?? [];

    public Task<bool> ApplyCodecAsync(TextCodec codec, TransformDirection direction) =>
        this.document.ApplyCodecAsync(codec, direction, EachLine ? CodecScope.EachLine : CodecScope.WholeText);

    public async Task<bool> ApplyRecipeAsync(RecipeEntry entry, TransformDirection direction)
    {
        if (this.recipes?.Load(entry) is not { } recipe)
        {
            ToastService.Show($"“{entry.DisplayName}” could not be read.");
            return false;
        }

        return await this.document.ApplyRecipeAsync(recipe, direction);
    }

    /// <summary>The recipe dialog's view model over this document, or null when there are no
    /// recipes or nothing loaded to preview.</summary>
    public RecipeEditorViewModel? CreateEditor(RecipeEntry? initial = null) =>
        this.recipes is { } store && this.document.Document is { } bytes
            ? new RecipeEditorViewModel(store, bytes, initial)
            : null;

    /// <summary>Applies what the dialog was closed with.</summary>
    public Task<bool> ApplyFromEditorAsync(RecipeEditorViewModel editor) =>
        this.document.ApplyRecipeAsync(editor.CurrentRecipe, editor.Direction);

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RawViewModel.CanSave) or nameof(RawViewModel.CanEdit)
            or nameof(RawViewModel.IsSaving) or nameof(RawViewModel.IsTransforming))
            OnPropertyChanged(nameof(CanTransform));
    }
}
