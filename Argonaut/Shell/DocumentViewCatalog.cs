using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

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
/// </summary>
public static class DocumentViewCatalog
{
    private static readonly (Func<IDocumentViewModel> Create,
        Func<IDocumentViewModel, FileTypeDetector.FileKind, string, IProgressReporter, Task> Load) [] Registrations =
    {
        (() => new JsonViewModel(), (vm, _, p, r) => ((JsonViewModel)vm).LoadAsync(p, r)),
        (() => new NdJsonViewModel(), (vm, _, p, r) => ((NdJsonViewModel)vm).LoadAsync(p, r)),
        (() => new CsvViewModel(), (vm, k, p, r) => ((CsvViewModel)vm).LoadAsync(p, k == FileTypeDetector.FileKind.Tsv ? (byte)'\t' : (byte)',', r)),
        (() => new RawViewModel(), (vm, _, p, r) => ((RawViewModel)vm).LoadAsync(p, r)),
    };

    // Display order doubles as the source of display names - one FileKind can only ever mean
    // one thing to the user, unlike the Registrations table where CSV/TSV share a view model.
    private static readonly (FileTypeDetector.FileKind Kind, string DisplayName)[] DisplayOrder =
    {
        (FileTypeDetector.FileKind.Json, "JSON"),
        (FileTypeDetector.FileKind.Ndjson, "NDJSON"),
        (FileTypeDetector.FileKind.Csv, "CSV"),
        (FileTypeDetector.FileKind.Tsv, "TSV"),
        (FileTypeDetector.FileKind.Unidentified, "Raw text"),
    };

    private static readonly IReadOnlyDictionary<FileTypeDetector.FileKind, int> KindToRegistration = BuildMap();

    /// <summary>All switchable views, in display order: JSON, NDJSON, CSV, TSV, Raw text.</summary>
    public static IReadOnlyList<DocumentViewOption> Options { get; } =
        DisplayOrder.Select(e => new DocumentViewOption(e.Kind, e.DisplayName)).ToArray();

    private static Dictionary<FileTypeDetector.FileKind, int> BuildMap()
    {
        var map = new Dictionary<FileTypeDetector.FileKind, int>();

        foreach (FileTypeDetector.FileKind kind in Enum.GetValues<FileTypeDetector.FileKind>())
        {
            if (kind == FileTypeDetector.FileKind.Unknown)
                continue;

            for (int i = 0; i < Registrations.Length; i++)
            {
                using var probe = Registrations[i].Create();
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
    public static async Task<IDocumentViewModel> LoadAsync(FileTypeDetector.FileKind kind, string path, IProgressReporter reporter)
    {
        var registration = Registrations[KindToRegistration[kind]];
        var vm = registration.Create();
        await registration.Load(vm, kind, path, reporter);
        return vm;
    }
}
