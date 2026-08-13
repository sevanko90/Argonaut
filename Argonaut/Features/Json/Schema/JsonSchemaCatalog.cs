using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Schema;

/// <summary>One selectable schema. <paramref name="DisplayName"/> is the file stem - schemas
/// are identified to the user by filename, with no need to open them.</summary>
public readonly record struct SchemaCatalogEntry(string DisplayName, string FilePath, bool IsUser);

/// <summary>
/// The list of schemas a document can be bound to, merged from the schemas shipped with the app
/// (<c>Schemas/</c> beside the executable) and the user's own
/// (<see cref="AppDataPaths.GetSchemasDirectory"/>). A user file shadows a bundled one of the
/// same name, so a shipped schema can be corrected locally without editing the install.
///
/// Enumeration never parses a schema - only <see cref="JsonSchemaSettings.SelectAsync"/> does,
/// on the file actually chosen - so a folder holding hundreds of schemas costs two directory
/// listings and nothing else.
/// </summary>
public static class JsonSchemaCatalog
{
    private const string FolderName = "Schemas";

    public static IReadOnlyList<SchemaCatalogEntry> Enumerate()
    {
        var byName = new Dictionary<string, SchemaCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        AddFolder(byName, GetBundledDirectory(), isUser: false);

        // Second, so a same-named user schema overwrites (shadows) the bundled one.
        AddFolder(byName, GetUserDirectory(), isUser: true);

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
    /// Pure filesystem work (two directory listings and a settings read) - call it off the UI
    /// thread.
    /// </summary>
    public static (IReadOnlyList<SchemaCatalogEntry> Entries, SchemaCatalogEntry? Preselected) GatherForDocument(string documentPath)
    {
        var entries = new List<SchemaCatalogEntry>(Enumerate());
        SchemaCatalogEntry? preselected = null;

        string sidecarPath = documentPath + SidecarSuffix;
        if (SafeExists(sidecarPath))
        {
            var sidecar = new SchemaCatalogEntry(Path.GetFileName(sidecarPath), sidecarPath, IsUser: true);
            entries.Insert(0, sidecar);
            preselected = sidecar;
        }
        else if (SchemaSelectionPreference.Load(documentPath) is { } remembered)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.FilePath, remembered, StringComparison.OrdinalIgnoreCase))
                {
                    preselected = entry;
                    break;
                }
            }

            // A remembered schema that has since left the catalog folders but still exists on
            // disk stays offered, rather than the binding silently disappearing.
            if (preselected is null && SafeExists(remembered))
            {
                var transient = new SchemaCatalogEntry(Path.GetFileNameWithoutExtension(remembered), remembered, IsUser: true);
                entries.Insert(0, transient);
                preselected = transient;
            }
        }

        return (entries, preselected);
    }

    /// <summary>Folder of schemas shipped with the app, beside the executable. Read-only in
    /// practice - an install directory isn't somewhere the user can save to, which is why
    /// <see cref="JsonSchemaExample"/> copies out of here rather than pointing at it.</summary>
    public static string GetBundledDirectory() => Path.Combine(AppContext.BaseDirectory, FolderName);

    public static string GetUserDirectory() => AppDataPaths.GetSchemasDirectory();

    /// <summary>Creates the user schema folder if it doesn't exist and returns its path.
    /// Returns the path either way - the caller only ever uses it to open a file manager, and a
    /// folder that couldn't be created is not worth an error dialog.</summary>
    public static string EnsureUserDirectory()
    {
        string path = GetUserDirectory();
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Best-effort, exactly like JsonSettingsStore.
        }

        return path;
    }

    /// <summary>
    /// Test seam: when set, replaces launching the OS file manager (see
    /// <see cref="AppDataPaths.RootOverride"/> for the same pattern). Null in production - tests
    /// set it so exercising the toolbar's "open schema folder" item doesn't pop a Finder/Explorer
    /// window on the build machine.
    /// </summary>
    internal static Action<string>? OpenDirectoryOverride;

    /// <summary>
    /// Creates the user schema folder, seeds it with the annotated example (see
    /// <see cref="JsonSchemaExample"/>) if that isn't already there, and reveals it in the OS file
    /// manager. Seeding happens here rather than in <see cref="EnsureUserDirectory"/> so it is
    /// tied to the user actually going to look at the folder.
    /// </summary>
    public static void OpenUserDirectory()
    {
        string path = EnsureUserDirectory();
        JsonSchemaExample.TryCopyTo(path);

        try
        {
            if (OpenDirectoryOverride is { } open)
                open(path);
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
