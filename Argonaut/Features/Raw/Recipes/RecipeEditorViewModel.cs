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

/// <summary>One row of the recipe list: a section heading, or a recipe.</summary>
public sealed record RecipeListRow(string Text, RecipeEntry? Entry)
{
    public bool IsHeading => Entry is null;
}

/// <summary>
/// The recipe dialog: a list of recipes (presets, then the user's own), the selected one's
/// settings, and the result on the start of the document as you type. Applying is the caller's -
/// the dialog only decides <see cref="CurrentRecipe"/> and <see cref="Direction"/>.
///
/// Edits to the settings are a draft until <see cref="SaveChanges"/>: choosing another recipe
/// drops them, as <see cref="Revert"/> does. Saving a preset's changes saves the user's own recipe
/// of the same name, which then stands in for the preset - deleting it brings the preset back.
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

    internal const string NewRecipeName = "New recipe";

    /// <summary>The escapes, in <see cref="TextEscape"/>'s order, as the combo shows them.</summary>
    public static IReadOnlyList<string> EscapeChoices { get; } =
    [
        "None",
        "SQL '",
        "CSV \"",
        "JSON \\",
    ];

    private readonly IRecipeStore store;
    private readonly byte[] previewInput;
    private readonly int previewInputLines;
    private readonly bool previewIsPartial;

    private Recipe loaded = new();
    private RecipeListRow? selectedRow;
    private string name = "";
    private string template = "";
    private int escapeIndex;
    private string separator = "";
    private string open = "";
    private string close = "";
    private bool trimLines;
    private bool skipBlankLines;
    private bool reverse;
    private string previewText = "";
    private string previewLineNumbers = "";
    private string? previewProblem;
    private string? storeProblem;

    /// <param name="document">The document the recipe will apply to; its start is read once,
    /// here, for the preview.</param>
    /// <param name="initial">The recipe selected on opening; the first one when null.</param>
    public RecipeEditorViewModel(IRecipeStore store, IByteSource document, RecipeEntry? initial = null)
    {
        this.store = store;
        (this.previewInput, this.previewInputLines, this.previewIsPartial) = ReadPreviewInput(document);

        Rows = new ObservableCollection<RecipeListRow>();
        Reload(initial);
        if (SelectedEntry is null)
            Load(new Recipe());
    }

    /// <summary>The list: a "Presets" heading and the shipped recipes, then "Yours" and the
    /// user's. A heading row is not selectable.</summary>
    public ObservableCollection<RecipeListRow> Rows { get; }

    /// <summary>
    /// Bound to the list. Loading the recipe into the fields neither closes a popup nor replaces
    /// <see cref="Rows"/>, so it needs no deferral (see CLAUDE.md); a heading is refused, keeping
    /// the selection where it was.
    /// </summary>
    public RecipeListRow? SelectedRow
    {
        get => this.selectedRow;
        set
        {
            if (value is { IsHeading: true } || !SetField(ref this.selectedRow, value))
                return;

            OnPropertyChanged(nameof(SelectedEntry));
            OnPropertyChanged(nameof(IsUserRecipe));
            StoreProblem = null;
            if (value?.Entry is { } entry)
                LoadEntry(entry);
        }
    }

    public RecipeEntry? SelectedEntry => SelectedRow?.Entry;

    /// <summary>The selected recipe is the user's own: it can be renamed and deleted.</summary>
    public bool IsUserRecipe => SelectedEntry is { IsUser: true };

    /// <summary>The selected recipe's name. Editable for the user's own recipes, and taking
    /// effect at <see cref="Rename"/>.</summary>
    public string Name
    {
        get => this.name;
        set => SetField(ref this.name, value);
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

            OnPropertyChanged(nameof(IsForward));
            OnPropertyChanged(nameof(ApplyLabel));
            RefreshPreview();
        }
    }

    /// <summary>The other half of the direction switch, so each of its buttons binds one way.</summary>
    public bool IsForward
    {
        get => !Reverse;
        set => Reverse = !value;
    }

    public TransformDirection Direction => Reverse ? TransformDirection.Reverse : TransformDirection.Forward;

    public string ApplyLabel => Reverse ? "Take apart" : "Apply";

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

    /// <summary>"1\n2\n…", one per line of <see cref="PreviewText"/>, for the gutter beside it.</summary>
    public string PreviewLineNumbers
    {
        get => this.previewLineNumbers;
        private set => SetField(ref this.previewLineNumbers, value);
    }

    /// <summary>Why there is no preview: the input does not fit the recipe. Applying would fail
    /// the same way.</summary>
    public string? PreviewProblem
    {
        get => this.previewProblem;
        private set => SetField(ref this.previewProblem, value);
    }

    public string PreviewCaption => this.previewIsPartial
        ? $"first {this.previewInputLines:N0} lines"
        : "whole document";

    /// <summary>Why the last save, rename or delete did not happen.</summary>
    public string? StoreProblem
    {
        get => this.storeProblem;
        private set => SetField(ref this.storeProblem, value);
    }

    /// <summary>Saves the fields over the selected recipe - as the user's own when it is a
    /// preset.</summary>
    public bool SaveChanges()
    {
        if (SelectedEntry is not { } entry)
            return false;

        return TrySave(entry.DisplayName, CurrentRecipe) is not null;
    }

    /// <summary>Puts the fields back to the selected recipe as saved.</summary>
    public void Revert()
    {
        if (SelectedEntry is { } entry)
            LoadEntry(entry);
    }

    /// <summary>Adds a recipe of the user's from the current fields, under a name not yet
    /// taken, and selects it - ready to be renamed.</summary>
    public bool CreateFromCurrent()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in this.store.Enumerate())
            taken.Add(entry.DisplayName);

        string candidate = NewRecipeName;
        for (int n = 2; taken.Contains(candidate); n++)
            candidate = $"{NewRecipeName} {n}";

        return TrySave(candidate, CurrentRecipe with { Description = null }) is not null;
    }

    /// <summary>
    /// Renames the selected user recipe to <see cref="Name"/>, carrying the fields as they are.
    /// Rebuilds <see cref="Rows"/>, so a caller reacting to focus leaving the name box must
    /// defer this past the input that moved it (see CLAUDE.md) - a click on the list would
    /// otherwise land on rows this has just replaced.
    /// </summary>
    public bool Rename()
    {
        if (SelectedEntry is not { IsUser: true } entry)
            return false;

        string wanted = Name.Trim();
        if (string.Equals(wanted, entry.DisplayName, StringComparison.Ordinal))
            return true;

        if (wanted.Length == 0)
        {
            Name = entry.DisplayName;
            return false;
        }

        foreach (var existing in this.store.Enumerate())
        {
            if (existing.IsUser && !existing.Equals(entry)
                && string.Equals(existing.DisplayName, wanted, StringComparison.OrdinalIgnoreCase))
            {
                StoreProblem = $"You already have a recipe called “{existing.DisplayName}”.";
                Name = entry.DisplayName;
                return false;
            }
        }

        var draft = CurrentRecipe;
        bool modified = IsModified;
        if (TrySave(wanted, this.loaded, select: false) is not { } renamed)
        {
            Name = entry.DisplayName;
            return false;
        }

        try
        {
            this.store.Delete(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreProblem = $"Saved as “{renamed.DisplayName}”, but the old one could not be removed: {ex.Message}";
        }

        Reload(renamed);
        if (modified)
            LoadDraft(draft);

        return true;
    }

    /// <summary>Deletes the selected user recipe - bringing back a preset it stood in for - and
    /// selects the first remaining recipe.</summary>
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
            StoreProblem = $"Could not delete: {ex.Message}";
            return;
        }

        Reload(null);
    }

    private RecipeEntry? TrySave(string displayName, Recipe recipe, bool select = true)
    {
        RecipeEntry saved;
        try
        {
            saved = this.store.Save(displayName, recipe);
        }
        catch (ArgumentException)
        {
            StoreProblem = "A recipe needs a name.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StoreProblem = $"Could not save: {ex.Message}";
            return null;
        }

        if (select)
            Reload(saved);

        return saved;
    }

    /// <summary>Re-lists the store and selects <paramref name="select"/> (matched by name and
    /// owner, since a save may have shadowed a preset), or the first recipe.</summary>
    private void Reload(RecipeEntry? select)
    {
        var presets = new List<RecipeEntry>();
        var mine = new List<RecipeEntry>();
        foreach (var entry in this.store.Enumerate())
            (entry.IsUser ? mine : presets).Add(entry);

        Rows.Clear();
        AddSection("Presets", presets);
        AddSection("Yours", mine);

        RecipeListRow? match = null, first = null;
        foreach (var row in Rows)
        {
            if (row.Entry is not { } entry)
                continue;

            first ??= row;
            if (select is { } wanted && entry.IsUser == wanted.IsUser
                && string.Equals(entry.DisplayName, wanted.DisplayName, StringComparison.OrdinalIgnoreCase))
                match = row;
        }

        this.selectedRow = null;
        SelectedRow = match ?? first;
    }

    private void AddSection(string heading, List<RecipeEntry> entries)
    {
        if (entries.Count == 0)
            return;

        Rows.Add(new RecipeListRow(heading, null));
        foreach (var entry in entries)
            Rows.Add(new RecipeListRow(entry.DisplayName, entry));
    }

    private void LoadEntry(RecipeEntry entry)
    {
        Name = entry.DisplayName;
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

    private void Load(Recipe recipe)
    {
        this.loaded = recipe;
        LoadDraft(recipe);
    }

    /// <summary>Puts <paramref name="recipe"/> in the fields without making it the saved state.</summary>
    private void LoadDraft(Recipe recipe)
    {
        this.template = RecipeFieldText.ToDisplay(recipe.Template);
        this.escapeIndex = (int)recipe.Escape;
        this.separator = RecipeFieldText.ToDisplay(recipe.Separator);
        this.open = RecipeFieldText.ToDisplay(recipe.Open);
        this.close = RecipeFieldText.ToDisplay(recipe.Close);
        this.trimLines = recipe.TrimLines;
        this.skipBlankLines = recipe.SkipBlankLines;

        foreach (string changed in new[] { nameof(Template), nameof(EscapeIndex), nameof(Separator), nameof(Open),
                     nameof(Close), nameof(TrimLines), nameof(SkipBlankLines), nameof(Description), nameof(IsModified) })
            OnPropertyChanged(changed);

        RefreshPreview();
    }

    private void SetSetting<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? changed = null)
    {
        if (!SetField(ref field, value, changed))
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
            PreviewLineNumbers = "";
            return;
        }

        PreviewProblem = null;
        string text = Encoding.UTF8.GetString(output.WrittenSpan);
        PreviewText = text.Length > PreviewOutputChars ? text[..PreviewOutputChars] + "\n…" : text;
        PreviewLineNumbers = LineNumbers(PreviewText);
    }

    private static string LineNumbers(string text)
    {
        if (text.Length == 0)
            return "";

        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
                lines++;
        }

        // A final newline ends the last line rather than starting another.
        if (text[^1] == '\n')
            lines--;

        var numbers = new StringBuilder(lines * 4);
        for (int i = 1; i <= lines; i++)
            numbers.Append(i).Append(i < lines ? "\n" : "");

        return numbers.ToString();
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
