using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the catalog's kind -> view model mapping (derived by probing
/// <see cref="IDocumentViewModel.CanHandleFileType"/>, not restated) covers every switchable
/// <see cref="FileTypeDetector.FileKind"/> exactly once, and that CSV/TSV route through the
/// same <see cref="CsvViewModel"/> with the correct delimiter.
/// </summary>
public class DocumentViewCatalogTests
{
    private static string WriteTempFile(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    [Fact]
    public void Options_CoversEveryKindExceptUnknown()
    {
        var kinds = DocumentViewCatalog.Options.Select(o => o.Kind).ToList();

        Assert.DoesNotContain(FileTypeDetector.FileKind.Unknown, kinds);
        Assert.Contains(FileTypeDetector.FileKind.Json, kinds);
        Assert.Contains(FileTypeDetector.FileKind.Ndjson, kinds);
        Assert.Contains(FileTypeDetector.FileKind.Csv, kinds);
        Assert.Contains(FileTypeDetector.FileKind.Tsv, kinds);
        Assert.Contains(FileTypeDetector.FileKind.Unidentified, kinds);
        Assert.Equal(5, kinds.Count);
    }

    [Fact]
    public void Options_DisplayNames_AreHumanReadable()
    {
        var byKind = DocumentViewCatalog.Options.ToDictionary(o => o.Kind, o => o.DisplayName);

        Assert.Equal("JSON", byKind[FileTypeDetector.FileKind.Json]);
        Assert.Equal("NDJSON", byKind[FileTypeDetector.FileKind.Ndjson]);
        Assert.Equal("CSV", byKind[FileTypeDetector.FileKind.Csv]);
        Assert.Equal("TSV", byKind[FileTypeDetector.FileKind.Tsv]);
        Assert.Equal("Raw text", byKind[FileTypeDetector.FileKind.Unidentified]);
    }

    [Theory]
    [InlineData(FileTypeDetector.FileKind.Json, typeof(JsonViewModel))]
    [InlineData(FileTypeDetector.FileKind.Ndjson, typeof(NdJsonViewModel))]
    [InlineData(FileTypeDetector.FileKind.Csv, typeof(CsvViewModel))]
    [InlineData(FileTypeDetector.FileKind.Tsv, typeof(CsvViewModel))]
    [InlineData(FileTypeDetector.FileKind.Unidentified, typeof(RawViewModel))]
    public async Task LoadAsync_ResolvesToTheViewModelThatClaimsTheKind(FileTypeDetector.FileKind kind, Type expectedType)
    {
        string path = WriteTempFile("""{"a":1}"""); // structurally valid enough for every loader to succeed
        try
        {
            var reporter = new NullProgressReporter();
            using var document = await DocumentViewCatalog.LoadAsync(kind, path, reporter);

            Assert.IsType(expectedType, document);
            Assert.True(document.CanHandleFileType(kind));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_Tsv_UsesTabDelimiter()
    {
        string path = WriteTempFile("a\tb\tc\n1\t2\t3\n");
        try
        {
            var reporter = new NullProgressReporter();
            using var document = await DocumentViewCatalog.LoadAsync(FileTypeDetector.FileKind.Tsv, path, reporter);

            var csv = Assert.IsType<CsvViewModel>(document);
            Assert.Equal((byte)'\t', csv.Delimiter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_Csv_UsesCommaDelimiter()
    {
        string path = WriteTempFile("a,b,c\n1,2,3\n");
        try
        {
            var reporter = new NullProgressReporter();
            using var document = await DocumentViewCatalog.LoadAsync(FileTypeDetector.FileKind.Csv, path, reporter);

            var csv = Assert.IsType<CsvViewModel>(document);
            Assert.Equal((byte)',', csv.Delimiter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public void Report(string message, long? current = null, long? max = null) { }
    }
}
