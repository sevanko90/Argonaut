using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Settings;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Ui.Documents;

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
        (FileTypeDetector.FileKind.Csv, "CSV"),
        (FileTypeDetector.FileKind.Tsv, "TSV"),
        (FileTypeDetector.FileKind.Unidentified, "Raw text"),
    };

    private readonly IReadOnlyDictionary<FileTypeDetector.FileKind, int> kindToRegistration;

    public DocumentViewCatalog(ISettingsStore settings, JsonSchemaCatalog schemaCatalog)
    {
        var jsonView = settings.Get<JsonViewSettings>();
        var schemaBindings = settings.Get<SchemaBindings>();
        var rawView = settings.Get<RawViewSettings>();

        registrations =
        [
            (() => new JsonViewModel(jsonView, schemaBindings, schemaCatalog), (vm, _, o, r) => ((JsonViewModel)vm).LoadAsync(o, r)),
            (() => new NdJsonViewModel(jsonView, schemaBindings, schemaCatalog), (vm, _, o, r) => ((NdJsonViewModel)vm).LoadAsync(o, r)),
            (() => new CsvViewModel(), (vm, k, o, r) => ((CsvViewModel)vm).LoadAsync(o, k == FileTypeDetector.FileKind.Tsv ? (byte)'\t' : (byte)',', r)),
            (() => new RawViewModel(rawView), (vm, _, o, r) => ((RawViewModel)vm).LoadAsync(o, r)),
        ];
        kindToRegistration = BuildMap();
    }

    /// <summary>All switchable views, in display order: JSON, NDJSON, CSV, TSV, Raw text.</summary>
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

    /// <summary>Builds and loads the document view model registered for <paramref name="kind"/>.</summary>
    public async Task<IDocumentViewModel> LoadAsync(FileTypeDetector.FileKind kind, IByteOrigin origin, IProgressReporter reporter)
    {
        var registration = registrations[kindToRegistration[kind]];
        var vm = registration.Create();
        await registration.Load(vm, kind, origin, reporter);
        return vm;
    }
}
