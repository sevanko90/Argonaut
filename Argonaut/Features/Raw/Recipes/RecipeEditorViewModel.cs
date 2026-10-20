using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Raw.Recipes;

/// <summary>
/// The recipe dialog: pick a recipe, change its settings, see the result on the start of the
/// document as you type, and save it as one of your own. Applying is the caller's - the dialog
/// only decides <see cref="CurrentRecipe"/> and <see cref="Direction"/>.
///
/// The preview re-runs on every change, synchronously, over at most
/// <see cref="PreviewInputBytes"/> of the document read once when the dialog opens - small
/// enough that a keystroke never waits on it, and fixed so that typing never reads the document
/// again.
/// </summary>
public sealed class RecipeEditorViewModel : ObservableObject
{
    internal const int PreviewLines = 50;
    internal const int PreviewInputBytes = 64 * 1024;
    internal const int PreviewOutputChars = 64 * 1024;

    /// <summary>The escapes, in <see cref="TextEscape"/>'s order, as the combo shows them.</summary>
    public static IReadOnlyList<string> EscapeChoices { get; } =
    [
        "None",
        "SQL — doubles '",
        "CSV — doubles \"",
        "JSON — backslash escapes",
    ];

    private readonly IRecipeStore store;
    private readonly byte[] previewInput;
    private readonly int previewInputLines;
    private readonly bool previewIsPartial;

    private Recipe loaded = new();
    private RecipeEntry? selectedEntry;
    private string template = "";
    private int escapeIndex;
    private string separator = "";
    private string open = "";
    private string close = "";
    private bool trimLines;
    private bool skipBlankLines;
    private bool reverse;
    private string previewText = "";
    private string? previewProblem;
    private string saveName = "";
    private string? saveProblem;

    /// <param name="document">The document the recipe will apply to; its start is read once,
    /// here, for the preview.</param>
    /// <param name="initial">The recipe selected on opening; the first one when null.</param>
    public RecipeEditorViewModel(IRecipeStore store, IByteSource document, RecipeEntry? initial = null)
    {
        this.store = store;
        (this.previewInput, this.previewInputLines, this.previewIsPartial) = ReadPreviewInput(document);

        Entries = new ObservableCollection<RecipeEntry>(store.Enumerate());
        SelectedEntry = initial is { } wanted && Entries.Contains(wanted) ? wanted
            : Entries.Count > 0 ? Entries[0]
            : null;

        if (SelectedEntry is null)
            Load(new Recipe());
    }

    public ObservableCollection<RecipeEntry> Entries { get; }

    /// <summary>Bound to the recipe combo. Loading the recipe into the fields neither closes a
    /// popup nor replaces <see cref="Entries"/>, so it needs no deferral (see CLAUDE.md).</summary>
    public RecipeEntry? SelectedEntry
    {
        get => this.selectedEntry;
        set
        {
            if (!SetField(ref this.selectedEntry, value))
                return;

            OnPropertyChanged(nameof(CanDelete));
            if (value is { } entry)
            {
                SaveName = entry.DisplayName;
                if (this.store.Load(entry) is { } recipe)
                {
                    Load(recipe);
                }
                else
                {
                    Load(new Recipe());
                    PreviewProblem = $"“{entry.DisplayName}” could not be read.";
                }
            }
        }
    }

    public string? Description => this.loaded.Description;

    public string Template
    {
        get => this.template;
        set => SetSetting(ref this.template, value);
    }

    /// <summary>Index into <see cref="EscapeChoices"/>.</summary>
    public int EscapeIndex
    {
        get => this.escapeIndex;
        set
        {
            if (value >= 0 && value < EscapeChoices.Count)
                SetSetting(ref this.escapeIndex, value);
        }
    }

    /// <summary>In display form (see <see cref="RecipeFieldText"/>), as are
    /// <see cref="Template"/>, <see cref="Open"/> and <see cref="Close"/>.</summary>
    public string Separator
    {
        get => this.separator;
        set => SetSetting(ref this.separator, value);
    }

    public string Open
    {
        get => this.open;
        set => SetSetting(ref this.open, value);
    }

    public string Close
    {
        get => this.close;
        set => SetSetting(ref this.close, value);
    }

    public bool TrimLines
    {
        get => this.trimLines;
        set => SetSetting(ref this.trimLines, value);
    }

    public bool SkipBlankLines
    {
        get => this.skipBlankLines;
        set => SetSetting(ref this.skipBlankLines, value);
    }

    /// <summary>Take a list apart instead of making one. Not part of the recipe: the same recipe
    /// is offered both ways.</summary>
    public bool Reverse
    {
        get => this.reverse;
        set
        {
            if (!SetField(ref this.reverse, value))
                return;

            OnPropertyChanged(nameof(ApplyLabel));
            RefreshPreview();
        }
    }

    public TransformDirection Direction => Reverse ? TransformDirection.Reverse : TransformDirection.Forward;

    public string ApplyLabel => Reverse ? "Reverse" : "Apply";

    /// <summary>The recipe as the fields now describe it.</summary>
    public Recipe CurrentRecipe => new()
    {
        Description = this.loaded.Description,
        Template = RecipeFieldText.FromDisplay(Template),
        Escape = (TextEscape)EscapeIndex,
        Separator = RecipeFieldText.FromDisplay(Separator),
        Open = RecipeFieldText.FromDisplay(Open),
        Close = RecipeFieldText.FromDisplay(Close),
        TrimLines = TrimLines,
        SkipBlankLines = SkipBlankLines,
    };

    /// <summary>The fields differ from the recipe as it was loaded or last saved.</summary>
    public bool IsModified => CurrentRecipe != this.loaded;

    public string PreviewText
    {
        get => this.previewText;
        private set => SetField(ref this.previewText, value);
    }

    /// <summary>Why there is no preview: the input does not fit the recipe. Applying would fail
    /// the same way.</summary>
    public string? PreviewProblem
    {
        get => this.previewProblem;
        private set => SetField(ref this.previewProblem, value);
    }

    public string PreviewCaption => this.previewIsPartial
        ? $"Preview · first {this.previewInputLines:N0} lines"
        : "Preview · whole document";

    public string SaveName
    {
        get => this.saveName;
        set
        {
            if (SetField(ref this.saveName, value))
                SaveProblem = null;
        }
    }

    public string? SaveProblem
    {
        get => this.saveProblem;
        private set => SetField(ref this.saveProblem, value);
    }

    /// <summary>Only the user's own recipes can be deleted; a preset can only be shadowed.</summary>
    public bool CanDelete => SelectedEntry is { IsUser: true };

    /// <summary>Saves the fields as the user's recipe <see cref="SaveName"/> and selects it.
    /// Returns false, with <see cref="SaveProblem"/> saying why, when it could not.</summary>
    public bool Save()
    {
        RecipeEntry saved;
        var recipe = CurrentRecipe;
        try
        {
            saved = this.store.Save(SaveName, recipe);
        }
        catch (ArgumentException)
        {
            SaveProblem = "Give the recipe a name.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveProblem = $"Could not save: {ex.Message}";
            return false;
        }

        Reload(saved);
        return true;
    }

    /// <summary>Deletes the selected user recipe and selects the first remaining one.</summary>
    public void Delete()
    {
        if (SelectedEntry is not { IsUser: true } entry)
            return;

        try
        {
            this.store.Delete(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveProblem = $"Could not delete: {ex.Message}";
            return;
        }

        Reload(null);
    }

    /// <summary>Re-lists the store and selects <paramref name="select"/> (matched by name, since
    /// a save may have shadowed a preset), or the first recipe.</summary>
    private void Reload(RecipeEntry? select)
    {
        Entries.Clear();
        foreach (var entry in this.store.Enumerate())
            Entries.Add(entry);

        RecipeEntry? match = null;
        foreach (var entry in Entries)
        {
            if (select is { } wanted && string.Equals(entry.DisplayName, wanted.DisplayName, StringComparison.OrdinalIgnoreCase))
                match = entry;
        }

        this.selectedEntry = null;
        SelectedEntry = match ?? (Entries.Count > 0 ? Entries[0] : null);
    }

    private void Load(Recipe recipe)
    {
        this.loaded = recipe;
        this.template = RecipeFieldText.ToDisplay(recipe.Template);
        this.escapeIndex = (int)recipe.Escape;
        this.separator = RecipeFieldText.ToDisplay(recipe.Separator);
        this.open = RecipeFieldText.ToDisplay(recipe.Open);
        this.close = RecipeFieldText.ToDisplay(recipe.Close);
        this.trimLines = recipe.TrimLines;
        this.skipBlankLines = recipe.SkipBlankLines;

        foreach (string name in new[] { nameof(Template), nameof(EscapeIndex), nameof(Separator), nameof(Open),
                     nameof(Close), nameof(TrimLines), nameof(SkipBlankLines), nameof(Description), nameof(IsModified) })
            OnPropertyChanged(name);

        RefreshPreview();
    }

    private void SetSetting<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name))
            return;

        OnPropertyChanged(nameof(IsModified));
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var output = new ArrayBufferWriter<byte>();
        try
        {
            RecipeRunner.Run(CurrentRecipe, Direction, new MemoryByteSource(this.previewInput), output, CancellationToken.None);
        }
        catch (TransformException ex)
        {
            PreviewProblem = ex.Message;
            PreviewText = "";
            return;
        }

        PreviewProblem = null;
        string text = Encoding.UTF8.GetString(output.WrittenSpan);
        PreviewText = text.Length > PreviewOutputChars ? text[..PreviewOutputChars] + "\n…" : text;
    }

    /// <summary>The start of the document, cut after <see cref="PreviewLines"/> lines or
    /// <see cref="PreviewInputBytes"/> bytes, whichever comes first.</summary>
    private static (byte[] Input, int Lines, bool Partial) ReadPreviewInput(IByteSource document)
    {
        var buffer = new byte[(int)Math.Min(PreviewInputBytes, document.AvailableLength)];
        int read = document.CopyTo(0, buffer);

        int lines = 0;
        int end = read;
        for (int i = 0; i < read; i++)
        {
            if (buffer[i] == (byte)'\n' && ++lines == PreviewLines)
            {
                end = i + 1;
                break;
            }
        }

        // A last line without its newline - the document's end, or the byte cap - counts too.
        if (end > 0 && buffer[end - 1] != (byte)'\n')
            lines++;

        return (buffer[..end], lines, end < document.AvailableLength);
    }
}
