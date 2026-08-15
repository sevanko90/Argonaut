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

    /// <summary>
    /// Which named root the *next* loaded schema should bind, held separately from
    /// <see cref="Document"/> because the two arrive independently: a remembered root name is
    /// known before its schema file has finished parsing, and it must survive a reselection of
    /// the same file. Cleared whenever a schema that doesn't offer that name is bound.
    /// </summary>
    private string? pendingRootName;

    /// <summary>
    /// Whether the bound root came from the user (picked in the flyout, or remembered from a
    /// previous session) rather than from this class defaulting. Only a defaulted root may be
    /// replaced when better evidence arrives - silently overriding a deliberate choice because
    /// indexing later disagreed would be indefensible.
    /// </summary>
    private bool rootExplicitlyChosen;

    private IReadOnlyList<SchemaRootMatch> rootMatches = Array.Empty<SchemaRootMatch>();
    private bool matchesDescribeArrayElements;

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

    /// <summary>
    /// The schemas the bound file offers as a root, or empty when it holds a single schema and
    /// the question doesn't arise. See <see cref="JsonSchemaDocument.NamedRoots"/>.
    /// </summary>
    public IReadOnlyList<SchemaRoot> RootOptions => Document?.NamedRoots ?? Array.Empty<SchemaRoot>();

    /// <summary>Name of the bound root, or null for the schema file's own root.</summary>
    public string? SelectedRootName => Document?.RootName;

    /// <summary>
    /// Whether <see cref="SelectedRootName"/> is the user's choice rather than a default this
    /// class landed on. Only a real choice is worth persisting: re-deriving a default on each
    /// open lets the match improve as the matcher does, where remembering one would freeze an
    /// early guess - in particular one made before indexing had gone far enough to score at all.
    /// </summary>
    public bool IsRootExplicitlyChosen => rootExplicitlyChosen;

    /// <summary>
    /// <see cref="RootOptions"/> scored against the open document's own property names, best
    /// first, or empty until the owning view model has sampled the document (it races indexing,
    /// so this arrives after the schema does). Ranking only - nothing here binds anything.
    /// </summary>
    public IReadOnlyList<SchemaRootMatch> RootMatches
    {
        get => rootMatches;
        private set => SetField(ref rootMatches, value);
    }

    /// <summary>Whether the document sampled for <see cref="RootMatches"/> is an array, so a
    /// match describes its elements rather than the document itself.</summary>
    public bool MatchesDescribeArrayElements
    {
        get => matchesDescribeArrayElements;
        private set => SetField(ref matchesDescribeArrayElements, value);
    }

    /// <summary>
    /// Pushes freshly-computed match scores in (see <see cref="RootMatches"/>) and, unless the
    /// user has chosen a root themselves, re-points the binding at the type those scores
    /// identify. Scores arrive after the schema does, so this is where a defaulted root gets
    /// upgraded from "the first one" to "the one that fits".
    /// </summary>
    public void SetRootMatches(IReadOnlyList<SchemaRootMatch> matches, bool describeArrayElements = false)
    {
        MatchesDescribeArrayElements = describeArrayElements;
        RootMatches = matches ?? Array.Empty<SchemaRootMatch>();

        if (rootExplicitlyChosen || Document is not { } current || current.NamedRoots.Count == 0)
            return;

        if (JsonSchemaRootMatcher.Best(RootMatches) is { } best)
            SetDocument(current.WithRoot(best.Name));
    }

    /// <summary>
    /// Binds a root for a schema that offers a choice but hasn't been given one. Nothing here can
    /// mislabel a document into an error - an unmatched key simply gets no hint - so landing on a
    /// usable default beats presenting an inert "no type" placeholder the user has to clear
    /// before the schema does anything. To see nothing, they pick "No schema".
    ///
    /// A schema whose own root is usable already has the right default (that root), so only a
    /// schema with no usable root of its own - an OpenAPI document - is defaulted here, to its
    /// first type. <see cref="SetRootMatches"/> upgrades that to the best match once scores exist.
    /// </summary>
    private JsonSchemaDocument? ApplyDefaultRoot(JsonSchemaDocument? value)
    {
        if (rootExplicitlyChosen || value is null || value.RootName is not null)
            return value;

        if (value.DocumentRootIsUsable || value.NamedRoots.Count == 0)
            return value;

        return value.WithRoot(value.NamedRoots[0].Name);
    }

    /// <summary>Whether "the schema file's own root" is a meaningful choice. False for an OpenAPI
    /// document - see <see cref="JsonSchemaDocument.DocumentRootIsUsable"/>.</summary>
    public bool DocumentRootIsUsable => Document?.DocumentRootIsUsable ?? false;

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
    public async Task SelectAsync(SchemaCatalogEntry? entry, string? rootName = null)
    {
        pendingRootName = rootName;

        // A remembered root was a user choice once, so it still counts as one.
        rootExplicitlyChosen = rootName is not null;
        SelectedEntry = entry;

        if (entry is not { } chosen)
        {
            SetDocument(null);
            return;
        }

        var loaded = await JsonSchemaLoader.LoadFileAsync(chosen.FilePath);

        if (!Equals(SelectedEntry, entry))
            return;

        SetDocument(loaded?.WithRoot(pendingRootName));
    }

    /// <summary>
    /// Binds one of <see cref="RootOptions"/> as the root the tree is labelled from, or the
    /// schema's own root with a null name. Synchronous - the schema is already parsed, so this
    /// only re-points the walk's starting node.
    /// </summary>
    public void SelectRoot(string? rootName)
    {
        pendingRootName = rootName;
        rootExplicitlyChosen = true;

        if (Document is { } current)
            SetDocument(current.WithRoot(rootName));
    }

    /// <summary>
    /// Pushes an already-parsed schema in directly. Used by NdJsonViewModel to share one parsed
    /// document across every per-line JsonViewModel instead of re-parsing the schema per line.
    /// </summary>
    public void SetDocument(JsonSchemaDocument? value)
    {
        value = ApplyDefaultRoot(value);

        if (ReferenceEquals(Document, value))
            return;

        // Scores belong to the schema they were computed against. A WithRoot rebind shares the
        // same root array and keeps them; binding a different schema file invalidates them, and
        // showing the previous file's scores until the recompute lands would be a lie.
        if (!ReferenceEquals(Document?.NamedRoots, value?.NamedRoots))
            RootMatches = Array.Empty<SchemaRootMatch>();

        Document = value;

        // WithRoot silently falls back to the document root for a name the schema doesn't have,
        // so take the bound name back from the document rather than trusting what was asked for -
        // otherwise a stale remembered root would be re-saved on every open.
        pendingRootName = value?.RootName;

        OnPropertyChanged(nameof(RootOptions));
        OnPropertyChanged(nameof(SelectedRootName));
        OnPropertyChanged(nameof(DocumentRootIsUsable));
        SchemaChanged?.Invoke(this, EventArgs.Empty);
    }
}
