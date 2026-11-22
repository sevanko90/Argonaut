using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Features.Hex.Structs;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Settings;
using Argonaut.Engine.Transforms;
using Argonaut.Features.Csv;
using Argonaut.Features.Hex;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Features.Xml;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Numbering;
using Argonaut.Ui.Launching;

namespace Argonaut.Shell;

/// <summary>One selectable entry in the view switcher, ordered for display.</summary>
/// <param name="Kind">The file kind this option forces the document to be loaded as.</param>
/// <param name="DisplayName">Label shown in the switcher.</param>
public sealed record DocumentViewOption(FileTypeDetector.FileKind Kind, string DisplayName);

/// <summary>
/// The single kind-to-view-model mapping in the app. Each registration's <c>Create</c>/<c>Load</c>
/// pair is probed once via <see cref="IDocumentViewModel.CanHandleFileType"/> to build the
/// <see cref="FileTypeDetector.FileKind"/> map, rather than restating the mapping - so adding a
/// new document view means adding one registration here, not touching a second switch elsewhere.
/// One registration can (and, for CSV/TSV, does) claim more than one <see cref="FileTypeDetector.FileKind"/>.
///
/// Also where each view model is handed the settings blocks and services it uses - the catalog
/// is the one place that constructs them, so it is the one place that needs the whole store.
/// </summary>
public sealed class DocumentViewCatalog
{
    private readonly (Func<IDocumentViewModel> Create,
        Func<IDocumentViewModel, FileTypeDetector.FileKind, IByteOrigin, IProgressReporter, Task> Load) [] registrations;

    // Display order doubles as the source of display names - one FileKind can only ever mean
    // one thing to the user, unlike the registrations table where CSV/TSV share a view model.
    private static readonly (FileTypeDetector.FileKind Kind, string DisplayName)[] DisplayOrder =
    {
        (FileTypeDetector.FileKind.Json, "JSON"),
        (FileTypeDetector.FileKind.Ndjson, "NDJSON"),
        (FileTypeDetector.FileKind.Xml, "XML"),
        (FileTypeDetector.FileKind.Csv, "CSV"),
        (FileTypeDetector.FileKind.Tsv, "TSV"),
        (FileTypeDetector.FileKind.Unidentified, "Raw text"),
        (FileTypeDetector.FileKind.Hex, "Hex"),
    };

    private readonly IReadOnlyDictionary<FileTypeDetector.FileKind, int> kindToRegistration;
    private readonly JsonSchemaCatalog schemaCatalog;
    private readonly ISystemLauncher? launcher;

    /// <param name="lineNumbers">The excerpt numbering switch the views follow - the shell's, so
    /// the control that flips it and the views that show it share one; a new one over
    /// <paramref name="settings"/> when null.</param>
    /// <param name="recipes">The recipes the raw view offers; null offers none.</param>
    /// <param name="launcher">Opens the links the views' value hints offer; null opens none.</param>
    /// <param name="settingsSealer">Seals the remembered schema choices; null keeps them for this
    /// run only.</param>
    /// <param name="structDefinitions">Where the hex view saves struct definitions; null offers
    /// no saving. The recent ones are sealed by <paramref name="settingsSealer"/> too.</param>
    /// <param name="exportActions">What the hex view's toolbar offers - saving a copy, opening in
    /// another app; null offers neither.</param>
    /// <param name="schemaMatcher">Measures the schema catalog against each JSON document; one
    /// over the catalog's files, kept for the catalog's life, when null.</param>
    public DocumentViewCatalog(ISettingsStore settings, JsonSchemaCatalog schemaCatalog, LineNumberingPreference? lineNumbers = null,
        IRecipeStore? recipes = null, ISystemLauncher? launcher = null, ISchemaMatcher? schemaMatcher = null,
        ISettingsSealer? settingsSealer = null, IDocumentExportActions? exportActions = null, IStructDefinitionStore? structDefinitions = null)
    {
        this.schemaCatalog = schemaCatalog;
        this.launcher = launcher;
        schemaMatcher ??= new CachedSchemaMatcher();
        lineNumbers ??= new LineNumberingPreference(settings.Get<LineNumberingSettings>());
        var jsonView = settings.Get<JsonViewSettings>();
        var xmlView = settings.Get<XmlViewSettings>();
        var schemaBindings = new SchemaBindings(settings.Get<SchemaBindingsBlock>(), settingsSealer);
        var rawView = settings.Get<RawViewSettings>();
        var hexView = settings.Get<HexViewSettings>();
        var recentStructs = new RecentStructDefinitions(settings.Get<RecentStructDefinitionsBlock>(), settingsSealer);

        registrations =
        [
            (() => new JsonViewModel(jsonView, schemaBindings, schemaCatalog, launcher, schemaMatcher), (vm, _, o, r) => ((JsonViewModel)vm).LoadAsync(o, r)),
            (() => new NdJsonViewModel(jsonView, schemaBindings, schemaCatalog, lineNumbers, launcher, schemaMatcher), (vm, _, o, r) => ((NdJsonViewModel)vm).LoadAsync(o, r)),
            (() => new XmlViewModel(xmlView, launcher), (vm, _, o, r) => ((XmlViewModel)vm).LoadAsync(o, r)),
            (() => new CsvViewModel(lineNumbers), (vm, k, o, r) => ((CsvViewModel)vm).LoadAsync(o, k == FileTypeDetector.FileKind.Tsv ? (byte)'\t' : (byte)',', r)),
            (() => new RawViewModel(rawView, lineNumbers: lineNumbers, recipes: recipes, launcher: launcher), (vm, _, o, r) => ((RawViewModel)vm).LoadAsync(o, r)),
            (() => new HexViewModel(exportActions, hexView, structDefinitions, recentStructs), (vm, _, o, _) => ((HexViewModel)vm).LoadAsync(o)),
        ];
        kindToRegistration = BuildMap();
    }

    /// <summary>All switchable views, in display order: JSON, NDJSON, XML, CSV, TSV, Raw text, Hex.</summary>
    public static IReadOnlyList<DocumentViewOption> Options { get; } =
        DisplayOrder.Select(e => new DocumentViewOption(e.Kind, e.DisplayName)).ToArray();

    private Dictionary<FileTypeDetector.FileKind, int> BuildMap()
    {
        var map = new Dictionary<FileTypeDetector.FileKind, int>();

        foreach (FileTypeDetector.FileKind kind in Enum.GetValues<FileTypeDetector.FileKind>())
        {
            if (kind == FileTypeDetector.FileKind.Unknown)
                continue;

            for (int i = 0; i < registrations.Length; i++)
            {
                using var probe = registrations[i].Create();
                if (probe.CanHandleFileType(kind))
                {
                    map[kind] = i;
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Builds and loads a small document hosted inside another view - a failed request's server
    /// message - rather than as the window's document: JSON or XML fully expanded, without schema
    /// matching (it would only be noise on an error) or opening an array as a table (which would
    /// replace the window's document), and anything else in the raw view. Settings are its own,
    /// never the user's.
    /// </summary>
    public async Task<IDocumentViewModel> LoadHostedAsync(FileTypeDetector.FileKind kind, IByteOrigin origin)
    {
        switch (kind)
        {
            case FileTypeDetector.FileKind.Json:
            {
                var json = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), schemaCatalog, launcher)
                {
                    ExpandsFully = true,
                    OffersArrayTable = false,
                };
                return await Loaded(json, json.LoadAsync(origin));
            }

            case FileTypeDetector.FileKind.Xml:
            {
                var xml = new XmlViewModel(new XmlViewSettings(), launcher) { ExpandsFully = true };
                return await Loaded(xml, xml.LoadAsync(origin));
            }

            default:
            {
                var raw = new RawViewModel(new RawViewSettings(), launcher: launcher);
                return await Loaded(raw, raw.LoadAsync(origin));
            }
        }

        static async Task<IDocumentViewModel> Loaded(IDocumentViewModel document, Task loading)
        {
            try
            {
                await loading;
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
    }

    /// <summary>Builds and loads the document view model registered for <paramref name="kind"/>.</summary>
    public async Task<IDocumentViewModel> LoadAsync(FileTypeDetector.FileKind kind, IByteOrigin origin, IProgressReporter reporter)
    {
        var registration = registrations[kindToRegistration[kind]];
        var vm = registration.Create();
        await registration.Load(vm, kind, origin, reporter);
        return vm;
    }
}
