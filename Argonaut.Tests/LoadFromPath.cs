using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Tests;

/// <summary>
/// Path-taking overloads of the document loads, for the many tests that write a temp file and
/// open it. They build exactly the <see cref="FileByteOrigin"/> the shell builds, so nothing is
/// faked - they only save every one of those tests restating it.
///
/// A test that is *about* origins (a paste with no path, growth, the lifetime rules) passes an
/// origin explicitly instead; these overloads are for the tests where the origin is incidental.
/// </summary>
internal static class LoadFromPath
{
    private static FileByteOrigin Origin(string path) => new(path);

    public static Task LoadAsync(this JsonViewModel vm, string path, IProgressReporter? progressReporter = null)
        => vm.LoadAsync(Origin(path), progressReporter);

    public static Task LoadAsync(this JsonViewModel vm, string path, long offset, long length,
        IProgressReporter? progressReporter = null)
        => vm.LoadAsync(Origin(path), offset, length, progressReporter);

    public static Task LoadAsync(this NdJsonViewModel vm, string path, IProgressReporter? progressReporter = null)
        => vm.LoadAsync(Origin(path), progressReporter);

    public static Task LoadAsync(this CsvViewModel vm, string path, byte delimiter,
        IProgressReporter? progressReporter = null)
        => vm.LoadAsync(Origin(path), delimiter, progressReporter);

    public static Task LoadAsync(this RawViewModel vm, string path, IProgressReporter? progressReporter = null)
        => vm.LoadAsync(Origin(path), progressReporter);

    public static Task LoadAsync(this JsonDiffViewModel vm, string leftPath, string rightPath)
        => vm.LoadAsync(Origin(leftPath), Origin(rightPath));

    public static Task LoadAsync(this JsonArrayTableViewModel vm, string path, long arrayOffset, long arrayLength,
        string arrayPath, Func<string, Task>? navigateBack = null)
        => vm.LoadAsync(Origin(path), arrayOffset, arrayLength, arrayPath, navigateBack);

    public static JsonDiffSession StartDiff(string leftPath, string rightPath,
        IProgressReporter? leftProgress = null, IProgressReporter? rightProgress = null,
        IProgressReporter? diffProgress = null)
        => JsonDiffSession.Start(Origin(leftPath), Origin(rightPath), leftProgress, rightProgress, diffProgress);

    public static JsonArrayTableSession StartArrayTable(string path, long offset, long length,
        IProgressReporter? progressReporter = null)
        => JsonArrayTableSession.Start(Origin(path), offset, length, progressReporter);

    public static ScanTarget ScanTargetFor(string path, long offset = 0, long length = -1)
        => new(Origin(path), offset, length);

    public static FileTypeDetector.FileKind DetectFileType(string path)
        => FileTypeDetector.DetectFileType(Origin(path));

    public static bool IsPlausibleFor(FileTypeDetector.FileKind kind, string path, out string reason)
        => FileTypeDetector.IsPlausibleFor(kind, Origin(path), out reason);

    public static Task<IDocumentViewModel> LoadDocumentAsync(FileTypeDetector.FileKind kind, string path,
        IProgressReporter reporter)
        => DocumentViewCatalog.LoadAsync(kind, Origin(path), reporter);
}
