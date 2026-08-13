using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Per-document session state for schema hints: the schemas offered in the toolbar, which one is
/// selected, and the parsed document behind that selection. A deliberate mirror of
/// <see cref="Hints.DateHintSettings"/> - not persisted itself (the *choice* is, via
/// <see cref="SchemaSelectionPreference"/>), lives and dies with the owning
/// JsonViewModel/NdJsonViewModel, UI-thread only.
/// </summary>
public sealed class JsonSchemaSettings : ObservableObject
{
    private IReadOnlyList<SchemaCatalogEntry> entries = Array.Empty<SchemaCatalogEntry>();
    private SchemaCatalogEntry? selectedEntry;
    private JsonSchemaDocument? document;

    /// <summary>Schemas offered for selection: the catalog, plus any transient sidecar entry
    /// discovered next to the open document.</summary>
    public IReadOnlyList<SchemaCatalogEntry> Entries
    {
        get => entries;
        private set => SetField(ref entries, value);
    }

    /// <summary>The chosen schema, or null for "no schema".</summary>
    public SchemaCatalogEntry? SelectedEntry
    {
        get => selectedEntry;
        private set => SetField(ref selectedEntry, value);
    }

    /// <summary>The parsed schema, or null when none is selected or the file couldn't be used.
    /// Note this stays null while a selection is still loading, and after a selection whose file
    /// failed to parse - by design, a bad schema shows nothing rather than complaining.</summary>
    public JsonSchemaDocument? Document
    {
        get => document;
        private set => SetField(ref document, value);
    }

    /// <summary>Raised whenever <see cref="Document"/> changes and rows therefore need rebuilding.
    /// Never raised for a no-op change.</summary>
    public event EventHandler? SchemaChanged;

    public void SetEntries(IReadOnlyList<SchemaCatalogEntry> value) => Entries = value;

    /// <summary>
    /// Selects a schema (or null to clear) and loads it off the UI thread. Awaiting this from the
    /// UI thread resumes there, per the app's threading convention.
    ///
    /// A selection made while an earlier one is still parsing wins: the stale load discards its
    /// result rather than overwriting the newer choice.
    /// </summary>
    public async Task SelectAsync(SchemaCatalogEntry? entry)
    {
        SelectedEntry = entry;

        if (entry is not { } chosen)
        {
            SetDocument(null);
            return;
        }

        var loaded = await JsonSchemaLoader.LoadFileAsync(chosen.FilePath);

        if (!Equals(SelectedEntry, entry))
            return;

        SetDocument(loaded);
    }

    /// <summary>
    /// Pushes an already-parsed schema in directly. Used by NdJsonViewModel to share one parsed
    /// document across every per-line JsonViewModel instead of re-parsing the schema per line.
    /// </summary>
    public void SetDocument(JsonSchemaDocument? value)
    {
        if (ReferenceEquals(Document, value))
            return;

        Document = value;
        SchemaChanged?.Invoke(this, EventArgs.Empty);
    }
}
