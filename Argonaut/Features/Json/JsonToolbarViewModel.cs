using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Header toolbar for the JSON tree view: date-hint scheme/time-zone radio groups (behind a
/// single "Date" dropdown button) bound to a document's <see cref="DateHintSettings"/>, the
/// default-expand-depth combo, and (JSON documents only) a "jump to JSONPath" text entry.
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

    private readonly DateHintSettings settings;
    private readonly JsonSchemaSettings schemaSettings;
    private readonly Action<int> applyExpandDepth;
    private readonly Func<string, Task>? navigateToPath;
    private int dateHintSchemeIndex;
    private int timeZoneModeIndex;
    private int expandDepthIndex;
    private string jsonPathInput = string.Empty;
    private IReadOnlyList<string> schemaItems = Array.Empty<string>();
    private int selectedSchemaIndex;

    public JsonToolbarViewModel(DateHintSettings settings, JsonSchemaSettings schemaSettings, int initialExpandDepthIndex, Action<int> applyExpandDepth, Func<string, Task>? navigateToPath = null)
    {
        this.settings = settings;
        this.schemaSettings = schemaSettings;
        this.applyExpandDepth = applyExpandDepth;
        this.navigateToPath = navigateToPath;

        dateHintSchemeIndex = (int)settings.FileDefaultScheme;
        timeZoneModeIndex = (int)settings.TimeZoneMode;
        expandDepthIndex = initialExpandDepthIndex;

        SchemaRootPicker = new SchemaRootPickerViewModel(schemaSettings);

        RebuildSchemaItems();

        settings.PropertyChanged += OnSettingsPropertyChanged;
        schemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;
    }

    /// <summary>Separates the schema file from the type within it in <see cref="SchemaButtonText"/>.</summary>
    public const string SchemaPathSeparator = " › ";

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

            return SchemaRootPicker.IsApplicable
                ? entry.DisplayName + SchemaPathSeparator + SchemaRootPicker.ButtonText
                : entry.DisplayName;
        }
    }

    /// <summary>Labels for the schema list: "No schema", one per catalog entry, then
    /// "Open schema folder…".</summary>
    public IReadOnlyList<string> SchemaItems
    {
        get => schemaItems;
        private set => SetField(ref schemaItems, value);
    }

    /// <summary>Bound two-way to the schema combo. Index 0 clears the binding, the trailing index
    /// opens the schema folder without changing the binding, and anything between selects the
    /// corresponding <see cref="JsonSchemaSettings.Entries"/> item.</summary>
    public int SelectedSchemaIndex
    {
        get => selectedSchemaIndex;
        set
        {
            // Avalonia briefly drives SelectedIndex to -1 while the item list is being swapped;
            // that isn't a user choice and must not clear the binding.
            if (value < 0 || !SetField(ref selectedSchemaIndex, value))
                return;

            if (value == schemaItems.Count - 1 && schemaItems.Count > 1)
            {
                JsonSchemaCatalog.OpenUserDirectory();
                SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex));
                return;
            }

            var entries = schemaSettings.Entries;
            _ = schemaSettings.SelectAsync(value >= 1 && value <= entries.Count ? entries[value - 1] : null);
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

    /// <summary>Bound two-way to the date-hint scheme combo; forwards to <see cref="DateHintSettings"/>.</summary>
    public int DateHintSchemeIndex
    {
        get => dateHintSchemeIndex;
        set
        {
            if (value < 0 || !SetField(ref dateHintSchemeIndex, value))
                return;

            settings.SetUserDefault((DateDecodingScheme)value);
        }
    }

    /// <summary>Bound two-way to the time-zone combo; forwards to <see cref="DateHintSettings"/>.</summary>
    public int TimeZoneModeIndex
    {
        get => timeZoneModeIndex;
        set
        {
            if (value < 0 || !SetField(ref timeZoneModeIndex, value))
                return;

            settings.SetTimeZoneMode((DateHintTimeZoneMode)value);
        }
    }

    /// <summary>Bound two-way to the expand-depth combo. Persists the choice and applies it
    /// live to the owning document's tree.</summary>
    public int ExpandDepthIndex
    {
        get => expandDepthIndex;
        set
        {
            if (value < 0 || !SetField(ref expandDepthIndex, value))
                return;

            ExpandDepthPreference.Save(value);
            applyExpandDepth(value);
        }
    }

    /// <summary>Inference completing in the background updates FileDefaultScheme - reflect it
    /// live in the combo.</summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(DateHintSettings.FileDefaultScheme) or nameof(DateHintSettings.TimeZoneMode))
            SyncFromSettings();
    }

    /// <summary>
    /// Pushes the current settings values into the bound combo indices. SetField's equality
    /// check makes this a no-op when nothing changed, so the resulting property notification
    /// doesn't loop back through the combo setters into <see cref="DateHintSettings"/>.
    /// </summary>
    private void SyncFromSettings()
    {
        SetField(ref dateHintSchemeIndex, (int)settings.FileDefaultScheme, nameof(DateHintSchemeIndex));
        SetField(ref timeZoneModeIndex, (int)settings.TimeZoneMode, nameof(TimeZoneModeIndex));
    }

    /// <summary>The catalog is populated asynchronously after the document opens, and a sidecar
    /// schema selects itself - both have to light up the combo without the user touching it.</summary>
    private void OnSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(JsonSchemaSettings.Entries))
            RebuildSchemaItems();
        else if (e.PropertyName is nameof(JsonSchemaSettings.SelectedEntry))
            SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex));

        // The button label spans both halves of the choice, so it has to follow either changing.
        if (e.PropertyName is null
            or nameof(JsonSchemaSettings.SelectedEntry)
            or nameof(JsonSchemaSettings.RootOptions)
            or nameof(JsonSchemaSettings.SelectedRootName))
            OnPropertyChanged(nameof(SchemaButtonText));
    }

    private void RebuildSchemaItems()
    {
        var entries = schemaSettings.Entries;
        var items = new List<string>(entries.Count + 2) { NoSchemaLabel };
        foreach (var entry in entries)
            items.Add(entry.DisplayName);
        items.Add(OpenSchemaFolderLabel);

        // Items first: the index only means anything against the list it indexes into.
        SchemaItems = items;
        SetField(ref selectedSchemaIndex, IndexOfSelectedEntry(), nameof(SelectedSchemaIndex));
    }

    private int IndexOfSelectedEntry()
    {
        if (schemaSettings.SelectedEntry is not { } selected)
            return 0;

        var entries = schemaSettings.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] == selected)
                return i + 1;
        }

        return 0;
    }
}
