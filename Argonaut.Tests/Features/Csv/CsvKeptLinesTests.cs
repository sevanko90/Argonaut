using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.NdJson;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Csv;

/// <summary>
/// A view switch closes a view and opens another over the same origin. A finished line index is
/// kept on the origin, so the next CSV or NDJSON view opens complete with no scan - the two share
/// it, since both read the same lines - unless the bytes changed since.
/// </summary>
public sealed class CsvKeptLinesTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"lines-{Guid.NewGuid():N}.csv");

    public void Dispose() => File.Delete(path);

    private static string Rows(int count)
    {
        var text = new StringBuilder("id,name\n");
        for (int i = 0; i < count; i++)
            text.Append(i).Append(",item").Append(i).Append('\n');
        return text.ToString();
    }

    /// <summary>The anchors kept on <paramref name="origin"/>, once the completion monitor has run
    /// - it resumes after the scan's task, so a test that only awaited that task can get here
    /// first.</summary>
    private static async Task<FileLineAnchors> KeptAnchorsAsync(IByteOrigin origin)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (IndexBasis.Of(origin)?.FindKept<FileLineAnchors>(FileLineAnchors.Key) is { } kept)
                return kept;

            await Task.Delay(10);
        }

        throw new TimeoutException("The finished scan's anchors were never kept.");
    }

    [Fact]
    public async Task LoadAsync_OverAnOriginAlreadyIndexed_ReusesItsAnchors()
    {
        File.WriteAllText(path, Rows(50_000));
        var origin = new FileByteOrigin(path);
        var first = new CsvViewModel();
        await first.LoadAsync(origin, (byte)',');
        await first.IndexingTask;
        var kept = await KeptAnchorsAsync(origin);
        first.Dispose();

        var second = new CsvViewModel();
        try
        {
            await second.LoadAsync(origin, (byte)',');

            Assert.True(second.IndexingTask.IsCompletedSuccessfully);
            Assert.Same(kept.Log, second.Index!.DetachAnchors()!.Log);
            Assert.Equal(50_001, second.Index.LineCount);
        }
        finally
        {
            second.Dispose();
            origin.Dispose();
        }
    }

    /// <summary>CSV and NDJSON read the same lines, so switching between them over one file
    /// scans it once.</summary>
    [Fact]
    public async Task NdJsonAfterCsv_ReusesTheSameAnchors()
    {
        File.WriteAllText(path, Rows(1000));
        var origin = new FileByteOrigin(path);
        var csv = new CsvViewModel();
        await csv.LoadAsync(origin, (byte)',');
        await csv.IndexingTask;
        var kept = await KeptAnchorsAsync(origin);
        csv.Dispose();

        var ndjson = new NdJsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
        try
        {
            await ndjson.LoadAsync(origin);

            Assert.True(ndjson.IndexingTask.IsCompletedSuccessfully);
            Assert.Same(kept.Log, ndjson.Index!.DetachAnchors()!.Log);
        }
        finally
        {
            ndjson.Dispose();
            origin.Dispose();
        }
    }

    /// <summary>Anchors for bytes that have since changed would put lines in the wrong places -
    /// past the end, on a shorter file - so it is scanned again.</summary>
    [Fact]
    public async Task LoadAsync_AfterTheFileChanged_ScansAgain()
    {
        File.WriteAllText(path, Rows(1000));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var origin = new FileByteOrigin(path);
        var first = new CsvViewModel();
        await first.LoadAsync(origin, (byte)',');
        await first.IndexingTask;
        var kept = await KeptAnchorsAsync(origin);
        first.Dispose();

        File.WriteAllText(path, Rows(3));

        var second = new CsvViewModel();
        try
        {
            await second.LoadAsync(origin, (byte)',');
            await second.IndexingTask;

            Assert.NotSame(kept.Log, second.Index!.DetachAnchors()!.Log);
            Assert.Equal(4, second.Index.LineCount);
        }
        finally
        {
            second.Dispose();
            origin.Dispose();
        }
    }
}
