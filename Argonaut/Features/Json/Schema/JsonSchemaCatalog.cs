using System;
using System.Collections.Generic;
using System.IO;

namespace Argonaut.Features.Json.Schema;

/// <summary>One selectable schema. <paramref name="DisplayName"/> is the file stem - schemas
/// are identified to the user by filename, with no need to open them.</summary>
public readonly record struct SchemaCatalogEntry(string DisplayName, string FilePath, bool IsUser);

/// <summary>
/// The list of schemas a document can be bound to, merged from the schemas shipped with the app
/// and the user's own folder. A user file shadows a bundled one of the same name, so a shipped
/// schema can be corrected locally without editing the install.
///
/// Both folders, and how a folder is shown to the user, are given to it by the composition root:
/// tests point it at temp folders, and a sandboxed build can point it somewhere else entirely.
///
/// Enumeration never parses a schema - only <see cref="JsonSchemaSettings.SelectAsync"/> does,
/// on the file actually chosen - so a folder holding hundreds of schemas costs two directory
/// listings and nothing else.
/// </summary>
/// <param name="bundledDirectory">Schemas shipped with the app. Read-only in practice - an
/// install directory isn't somewhere the user can save to, which is why
/// <see cref="JsonSchemaExample"/> copies out of here rather than pointing at it.</param>
/// <param name="userDirectory">Where the user drops their own schemas. Not created until
/// <see cref="EnsureUserDirectory"/> - enumeration tolerates it being absent.</param>
/// <param name="revealDirectory">Shows a folder to the user - the OS file manager, in the app.</param>
public sealed class JsonSchemaCatalog(string bundledDirectory, string userDirectory, Action<string> revealDirectory)
{
    /// <summary>The bundled folder's name beside the executable.</summary>
    public const string BundledFolderName = "Schemas";

    /// <summary>The bundled folder as the app ships it, beside the executable.</summary>
    public static string BundledDirectoryBesideApp => Path.Combine(AppContext.BaseDirectory, BundledFolderName);

    public string BundledDirectory => bundledDirectory;

    public string UserDirectory => userDirectory;

    public IReadOnlyList<SchemaCatalogEntry> Enumerate()
    {
        var byName = new Dictionary<string, SchemaCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        AddFolder(byName, bundledDirectory, isUser: false);

        // Second, so a same-named user schema overwrites (shadows) the bundled one.
        AddFolder(byName, userDirectory, isUser: true);

        var entries = new List<SchemaCatalogEntry>(byName.Values);
        entries.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>Suffix that makes a schema file the sidecar of the document beside it:
    /// <c>orders.json</c> is documented by <c>orders.json.schema.json</c>.</summary>
    public const string SidecarSuffix = ".schema.json";

    /// <summary>
    /// Suffix marking a schema as reference material rather than something to bind to. Files
    /// named this way are skipped by <see cref="Enumerate"/> and so never reach the dropdown -
    /// which is what lets <see cref="JsonSchemaExample"/> live in the user's schema folder as a
    /// worked example without cluttering the list. Copy such a file to a name without the suffix
    /// to actually use it.
    /// </summary>
    public const string ExampleSuffix = ".example.json";

    /// <summary>
    /// The catalog as offered for one specific document, plus the entry that should be bound
    /// immediately: a <c>&lt;file&gt;.schema.json</c> sidecar if there is one, otherwise the
    /// schema last bound to this path. Both a sidecar and a remembered schema outside the
    /// catalog folders are added as transient entries, so the combo can always show what's bound.
    ///
    /// Pure filesystem work (two directory listings) - call it off the UI thread.
    /// </summary>
    /// <param name="documentPath">
    /// The document's own path, or null when it has no path on disk - a clipboard paste, or a
    /// download served from memory. Both of the document-specific bindings are keyed by path (the
    /// <c>&lt;file&gt;.schema.json</c> sidecar, and the schema last chosen for this path), so
    /// with no path there is nothing to preselect and the user folder's schemas are all that can
    /// be offered. That is a real, usable result rather than an error: the user can still pick a
    /// schema by hand, it just cannot be remembered for next time.
    /// </param>
    /// <param name="bindings"><see cref="SchemaBindings.Entries"/>, read on the caller's thread and
    /// passed in as a snapshot.</param>
    public (IReadOnlyList<SchemaCatalogEntry> Entries, SchemaCatalogEntry? Preselected, string? RootName) GatherForDocument(string? documentPath, IReadOnlyList<SchemaBinding> bindings)
    {
        var entries = new List<SchemaCatalogEntry>(Enumerate());
        SchemaCatalogEntry? preselected = null;
        string? rootName = null;

        if (documentPath is null)
            return (entries, null, null);

        string sidecarPath = documentPath + SidecarSuffix;
        if (SafeExists(sidecarPath))
        {
            var sidecar = new SchemaCatalogEntry(Path.GetFileName(sidecarPath), sidecarPath, IsUser: true);
            entries.Insert(0, sidecar);
            preselected = sidecar;
        }
        else if (SchemaBindings.Find(bindings, documentPath) is { } remembered)
        {
            // Carried even when the schema file itself can't be found: harmless if unused, and
            // the loader drops a root name the schema no longer offers.
            rootName = remembered.RootName;

            foreach (var entry in entries)
            {
                if (string.Equals(entry.FilePath, remembered.SchemaPath, StringComparison.OrdinalIgnoreCase))
                {
                    preselected = entry;
                    break;
                }
            }

            // A remembered schema that has since left the catalog folders but still exists on
            // disk stays offered, rather than the binding silently disappearing.
            if (preselected is null && SafeExists(remembered.SchemaPath))
            {
                var transient = new SchemaCatalogEntry(Path.GetFileNameWithoutExtension(remembered.SchemaPath), remembered.SchemaPath, IsUser: true);
                entries.Insert(0, transient);
                preselected = transient;
            }
        }

        return (entries, preselected, rootName);
    }

    /// <summary>Creates the user schema folder if it doesn't exist and returns its path.
    /// Returns the path either way - the caller only ever uses it to open a file manager, and a
    /// folder that couldn't be created is not worth an error dialog.</summary>
    public string EnsureUserDirectory()
    {
        try
        {
            Directory.CreateDirectory(userDirectory);
        }
        catch
        {
            // Best-effort, exactly like SettingsStore.
        }

        return userDirectory;
    }

    /// <summary>
    /// Creates the user schema folder, seeds it with the annotated example (see
    /// <see cref="JsonSchemaExample"/>) if that isn't already there, and reveals it in the OS file
    /// manager. Seeding happens here rather than in <see cref="EnsureUserDirectory"/> so it is
    /// tied to the user actually going to look at the folder.
    /// </summary>
    public void OpenUserDirectory()
    {
        string path = EnsureUserDirectory();
        JsonSchemaExample.TryCopy(bundledDirectory, path);

        try
        {
            revealDirectory(path);
        }
        catch
        {
            // No file manager, or the folder couldn't be created - nothing useful to say.
        }
    }

    private static void AddFolder(Dictionary<string, SchemaCatalogEntry> byName, string directory, bool isUser)
    {
        foreach (string file in SafeGetFiles(directory))
        {
            if (file.EndsWith(ExampleSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            string name = Path.GetFileNameWithoutExtension(file);
            byName[name] = new SchemaCatalogEntry(name, file, isUser);
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string[] SafeGetFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : Array.Empty<string>();
        }
        catch
        {
            // An unreadable or vanished folder simply contributes no schemas.
            return Array.Empty<string>();
        }
    }
}
