using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Engine.Hints;
using Argonaut.Ui.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Ui.Tree;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Json;

/// <summary>
/// Header toolbar for the JSON tree view: the shared date-hint controls
/// (<see cref="DateHintToolbarState"/>: scheme and time zone), the shared tree controls (<see cref="TreeToolbarState"/>: expand depth, indent guides), and (JSON documents only) a "jump to JSONPath" text entry.
/// Shared by JsonViewModel and NdJsonViewModel, which expose an identical surface (a
/// DateHintSettings instance and a SetDefaultExpandDepth callback) and previously drove these
/// same combos through the shell via type-switches. NdJsonViewModel omits
/// <paramref name="navigateToPath"/> (path navigation isn't a per-line NDJSON concept), which
/// hides the path entry via <see cref="SupportsPathNavigation"/>.
///
/// Owned by the document view model that creates it (see <see cref="JsonViewModel.Toolbar"/> /
/// NdJsonViewModel's equivalent) and shares its lifetime - no unsubscription is needed since
/// this and the settings object it subscribes to are disposed together.
/// </summary>
public sealed class JsonToolbarViewModel : ObservableObject
{
    /// <summary>First entry of the schema combo: "no schema bound".</summary>
    public const string NoSchemaLabel = "No schema";

    /// <summary>Last entry of the schema combo. An action rather than a selection - picking it
    /// opens the user schema folder and reverts the combo to whatever was selected before.</summary>
    public const string OpenSchemaFolderLabel = "Open schema folder…";

    /// <summary>Heads the schemas that fit the open document, best first.</summary>
    public const string LikelySchemasHeader = "Likely matches for this document";

    /// <summary>Heads the rest of the catalog, below the likely matches.</summary>
    public const string AllSchemasHeader = "All schemas";

    /// <summary>Follows the schema's name on the button when it was matched rather than chosen.</summary>
    public const string MatchedSuffix = " · matched";

    private readonly JsonSchemaSettings schemaSettings;
    private readonly Func<string, Task>? navigateToPath;
    private readonly Func<Task>? refreshSchemaEntries;
    private readonly Action? openSchemaFolder;
    private readonly Action<bool>? applyOpenMatchedSchemas;
    private string jsonPathInput = string.Empty;
    private IReadOnlyList<SchemaListRow> schemaItems = Array.Empty<SchemaListRow>();
    private bool openMatchedSchemas;
    private int selectedSchemaIndex;
    private bool isSchemaFlyoutOpen;

    // Set when a schema is picked and the flyout is deliberately left open until we know whether
    // the type within it is obvious. Cleared as soon as the flyout closes, so match scores
    // arriving later - indexing completes long after the fact - can never shut a flyout the user
    // has since reopened to browse.
    private bool awaitingSchemaCloseDecision;

    /// <param name="openMatchedSchemas">Whether "Open matched schemas" starts ticked.</param>
    /// <param name="applyOpenMatchedSchemas">Told when the option is ticked or unticked; null hides
    /// the option, for an owner that does not match schemas.</param>
    public JsonToolbarViewModel(DateHintSettings settings, JsonSchemaSettings schemaSettings, int initialExpandDepthIndex, Action<int> applyExpandDepth, Func<string, Task>? navigateToPath = null, Func<Task>? refreshSchemaEntries = null, Action? openSchemaFolder = null, bool showIndentGuides = false, Action<bool>? applyIndentGuides = null,
        bool openMatchedSchemas = false, Action<bool>? applyOpenMatchedSchemas = null)
    {
        this.openMatchedSchemas = openMatchedSchemas;
        this.applyOpenMatchedSchemas = applyOpenMatchedSchemas;
        DateHintControls = new DateHintToolbarState(settings);
        this.schemaSettings = schemaSettings;
        this.navigateToPath = navigateToPath;
        this.refreshSchemaEntries = refreshSchemaEntries;
        this.openSchemaFolder = openSchemaFolder;
        TreeControls = new TreeToolbarState(initialExpandDepthIndex, applyExpandDepth, showIndentGuides, applyIndentGuides);

        SchemaRootPicker = new SchemaRootPickerViewModel(schemaSettings, closeRequested: () => IsSchemaFlyoutOpen = false);

        RebuildSchemaItems();

        schemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;
    }

    /// <summary>Separates the schema file from the type within it in <see cref="SchemaButtonText"/>.</summary>
    public const string SchemaPathSeparator = " › ";

    /// <summary>
    /// Two-way bound to the schema flyout's <c>IsOpen</c>, which is how it closes: a bool the view
    /// binds, rather than the view model reaching into the view to hide a popup.
    ///
    /// Picking a schema does *not* close it. Which type within that schema fits this document is
    /// only known once the file has parsed and its types have been scored, which happens a beat
    /// later and on another object - so the flyout stays open until the answer arrives and closes
    /// itself only when that answer is unambiguous. Everything that could go wrong (a schema that
    /// fails to parse, a document not yet indexed far enough to sample, an NDJSON file with no
    /// line open) simply leaves it open, which is why no timeout is needed anywhere.
    ///
    /// Opening it also re-reads the user's schema folder (<see cref="refreshSchemaEntries"/>) -
    /// otherwise a schema dropped in after the document was opened only appeared on restart,
    /// since the catalog is gathered once, at open time.
    /// </summary>
    public bool IsSchemaFlyoutOpen
    {
        get => isSchemaFlyoutOpen;
        set
        {
            if (!SetField(ref isSchemaFlyoutOpen, value))
                return;

            if (value)
            {
                _ = refreshSchemaEntries?.Invoke();
                return;
            }

            awaitingSchemaCloseDecision = false;
            SchemaRootPicker.Filter = string.Empty;
        }
    }

    /// <summary>
    /// What the schema button reads when closed: the bound schema, and the type within it when the
    /// schema offers a choice.
    ///
    /// The file and the type are one question - "what describes this document" - and were two
    /// controls only because they were built at different times. Answering it in one place is both
    /// narrower on the toolbar and fewer clicks for the common flow of picking a schema and then a
    /// type, which previously meant two separate popups.
    /// </summary>
    public string SchemaButtonText
    {
        get
        {
            if (schemaSettings.SelectedEntry is not { } entry)
                return NoSchemaLabel;

            string text = SchemaRootPicker.IsApplicable
                ? entry.DisplayName + SchemaPathSeparator + SchemaRootPicker.ButtonText
                : entry.DisplayName;

            // A schema nobody chose has to say why it is there, or it reads as a bug.
            return schemaSettings.IsMatched ? text + MatchedSuffix : text;
        }
    }

    /// <summary>Whether the owner matches schemas to its document, and so offers
    /// <see cref="OpenMatchedSchemas"/>.</summary>
    public bool SupportsSchemaMatching => applyOpenMatchedSchemas is not null;

    /// <summary>
    /// "Open matched schemas": whether a document opened with nothing bound is given the catalog
    /// schema that clearly fits it. Ticking it on also tries now, for this document - see the
    /// owner's callback. Unticking it leaves a matched schema bound.
    /// </summary>
    public bool OpenMatchedSchemas
    {
        get => openMatchedSchemas;
        set
        {
            if (SetField(ref openMatchedSchemas, value))
                applyOpenMatchedSchemas?.Invoke(value);
        }
    }

    /// <summary>The schema list: "No schema"; then, when schemas fit the document, those under
    /// <see cref="LikelySchemasHeader"/> best first and the rest under <see cref="AllSchemasHeader"/>,
    /// otherwise the catalog as it is; then "Open schema folder…".</summary>
    public IReadOnlyList<SchemaListRow> SchemaItems
    {
        get => schemaItems;
        private set => SetField(ref schemaItems, value);
    }

    /// <summary>Bound two-way to the schema list. Index 0 clears the binding, the trailing index
    /// opens the schema folder without changing the binding, a heading does nothing, and a schema
    /// row selects that schema.
    ///
    /// The choice is *decided* here and *acted on* one dispatcher turn later: every branch either
    /// closes the flyout or rebuilds <see cref="SchemaItems"/>, and this setter runs inside the
    /// list's own selection commit for the click that made the choice. See
    /// <see cref="UiDeferral"/> for why doing either inline crashes the process.</summary>
    public int SelectedSchemaIndex
    {
        get => selectedSchemaIndex;
        set
        {
            // Avalonia briefly drives SelectedIndex to -1 while the item list is being swapped;
            // that isn't a user choice and must not clear the binding.
            if (value < 0 || !SetField(ref selectedSchemaIndex, value))
                return;

            // Resolved now, against the list the user actually clicked in - by the time the
            // deferred body runs, entries may have been rebuilt underneath it.
            var row = value < schemaItems.Count ? schemaItems[value] : null;
            if (row is null || row.IsHeader)
            {
                UiDeferral.AfterCurrentInput(() =>
                    SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex)));
                return;
            }

            bool openFolder = value == schemaItems.Count - 1 && schemaItems.Count > 1;
            SchemaCatalogEntry? chosen = row.Entry;
            bool clearing = !openFolder && chosen is null;

            UiDeferral.AfterCurrentInput(() =>
            {
                if (openFolder)
                {
                    openSchemaFolder?.Invoke();
                    SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex));
                    IsSchemaFlyoutOpen = false;
                    return;
                }

                _ = schemaSettings.SelectAsync(chosen);

                // Clearing the binding leaves nothing further to choose; picking one leaves the
                // type open until the scores say whether it needs choosing at all.
                if (clearing)
                    IsSchemaFlyoutOpen = false;
                else
                    awaitingSchemaCloseDecision = true;
            });
        }
    }

    /// <summary>
    /// The schema-type picker shown beside the schema combo, for a schema file that holds several
    /// independently-usable schemas. Always present; hides itself via
    /// <see cref="SchemaRootPickerViewModel.IsApplicable"/> when the bound schema holds only one.
    /// </summary>
    public SchemaRootPickerViewModel SchemaRootPicker { get; }

    /// <summary>Whether the "jump to JSONPath" text entry should be shown - false for the
    /// shared NDJSON toolbar, which has no single-document JSONPath concept.</summary>
    public bool SupportsPathNavigation => navigateToPath is not null;

    /// <summary>Bound two-way to the "jump to path" text entry.</summary>
    public string JsonPathInput
    {
        get => jsonPathInput;
        set => SetField(ref jsonPathInput, value);
    }

    /// <summary>Resolves <see cref="JsonPathInput"/> and navigates to it; no-op if path
    /// navigation isn't supported or the box is blank.</summary>
    public Task GoToPathAsync()
    {
        if (navigateToPath is null || string.IsNullOrWhiteSpace(jsonPathInput))
            return Task.CompletedTask;

        return navigateToPath(jsonPathInput);
    }

    /// <summary>The date-hint scheme and time-zone choices every tree toolbar shares.</summary>
    public DateHintToolbarState DateHintControls { get; }

    /// <summary>The expand-depth and indent-guides controls every tree toolbar shares.</summary>
    public TreeToolbarState TreeControls { get; }

    /// <summary>The catalog is populated asynchronously after the document opens, and a remembered
    /// schema selects itself - both have to light up the combo without the user touching it.</summary>
    private void OnSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(JsonSchemaSettings.Entries) or nameof(JsonSchemaSettings.Fits))
            RebuildSchemaItems();
        else if (e.PropertyName is nameof(JsonSchemaSettings.SelectedEntry))
            SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex));

        // The button label spans both halves of the choice, so it has to follow either changing.
        if (e.PropertyName is null
            or nameof(JsonSchemaSettings.SelectedEntry)
            or nameof(JsonSchemaSettings.RootOptions)
            or nameof(JsonSchemaSettings.SelectedRootName)
            or nameof(JsonSchemaSettings.IsMatched))
            OnPropertyChanged(nameof(SchemaButtonText));

        if (e.PropertyName is null
            or nameof(JsonSchemaSettings.RootOptions)
            or nameof(JsonSchemaSettings.RootMatches))
            ConsiderClosingSchemaFlyout();
    }

    /// <summary>
    /// Closes the flyout left open by a schema pick, once there is nothing left worth showing:
    /// either the schema offers no type to choose (a single-schema file, or one that failed to
    /// parse), or the scores identify one clearly.
    ///
    /// Several equally-good candidates deliberately leave it open - that is the case the user has
    /// to settle, and closing would hide the very list they need.
    /// </summary>
    private void ConsiderClosingSchemaFlyout()
    {
        if (!awaitingSchemaCloseDecision || !isSchemaFlyoutOpen)
            return;

        if (schemaSettings.RootOptions.Count == 0 || JsonSchemaRootMatcher.Best(schemaSettings.RootMatches) is not null)
            IsSchemaFlyoutOpen = false;
    }

    private void RebuildSchemaItems()
    {
        var entries = schemaSettings.Entries;
        var fits = schemaSettings.Fits;
        var rows = new List<SchemaListRow>(entries.Count + 4) { SchemaListRow.Action(NoSchemaLabel) };

        // Only fits still in the catalog: a list refreshed since the measurement may have lost one.
        var listed = new HashSet<SchemaCatalogEntry>(entries);
        var ranked = new List<SchemaFit>(fits.Count);
        foreach (var fit in fits)
        {
            if (listed.Contains(fit.Entry))
                ranked.Add(fit);
        }

        if (ranked.Count == 0)
        {
            foreach (var entry in entries)
                rows.Add(SchemaListRow.Schema(entry));
        }
        else
        {
            rows.Add(SchemaListRow.Header(LikelySchemasHeader));
            var shown = new HashSet<SchemaCatalogEntry>();
            foreach (var fit in ranked)
            {
                rows.Add(SchemaListRow.Schema(fit.Entry, fit.Match, isRecommended: fit.Entry == schemaSettings.RecommendedFit));
                shown.Add(fit.Entry);
            }

            if (shown.Count < entries.Count)
            {
                rows.Add(SchemaListRow.Header(AllSchemasHeader, showSeparator: true));
                foreach (var entry in entries)
                {
                    if (!shown.Contains(entry))
                        rows.Add(SchemaListRow.Schema(entry));
                }
            }
        }

        rows.Add(SchemaListRow.Action(OpenSchemaFolderLabel));

        // Items first: the index only means anything against the list it indexes into.
        SchemaItems = rows;
        SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex));
    }

    private int IndexOfSelectedEntry()
    {
        if (schemaSettings.SelectedEntry is not { } selected)
            return 0;

        for (int i = 0; i < schemaItems.Count; i++)
        {
            if (schemaItems[i].Entry == selected)
                return i;
        }

        return 0;
    }
}
