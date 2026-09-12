using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The origin seam: a document whose bytes never were a file loads, indexes, sub-documents and
/// searches exactly like one that was, and the path-shaped features degrade rather than inventing
/// a path. These are the tests that would have to be rewritten if the origin ever collapsed back
/// into the byte source, so they are also the ones pinning the two lifetimes apart.
/// </summary>
public class ByteOriginTests
{
    private static MemoryByteOrigin Pasted(string text)
        => new(Encoding.UTF8.GetBytes(text), "Pasted text");

    // ---- identity ----------------------------------------------------------------------

    [Fact]
    public void AFileOrigin_ReportsItsPathAndFileName()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var origin = new FileByteOrigin(path);

            Assert.Equal(Path.GetFullPath(path), origin.Path);
            Assert.Equal(Path.GetFileName(path), origin.DisplayName);
            Assert.Equal(2, origin.AvailableLength);
            Assert.True(origin.LengthSettled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APastedOrigin_HasNoPathButStillHasAName()
    {
        var origin = Pasted("{\"a\":1}");

        Assert.Null(origin.Path);
        Assert.Equal("Pasted text", origin.DisplayName);
        Assert.Equal(7, origin.AvailableLength);
        Assert.True(origin.LengthSettled);
    }

    // ---- sources over an origin --------------------------------------------------------

    [Fact]
    public void OpenRange_IsIndependentOfTheWholeDocumentSourceAndZeroBased()
    {
        var origin = Pasted("{\"a\":1}\n{\"b\":2}\n");

        var whole = origin.Open();
        var line = origin.OpenRange(8, 7);

        Assert.Equal(16, whole.AvailableLength);
        Assert.Equal(7, line.AvailableLength);
        Assert.Equal("{\"b\":2}", line.GetUtf8String(0, 7));

        // Releasing one leaves the other usable - the point of every caller owning its own.
        line.Release();
        Assert.Equal(16, whole.AvailableLength);
        whole.Release();
    }

    [Fact]
    public void ASubRangeIsAlwaysSettled_BecauseItsBytesHaveAlreadyArrived()
    {
        var origin = Pasted("{\"a\":1}");

        Assert.True(origin.OpenRange(1, 3).LengthSettled);
    }

    [Fact]
    public void TwoSourcesOverOneOrigin_CoexistAsAViewSwapNeeds()
    {
        // A view swap loads the incoming document before disposing the outgoing one, so both
        // hold a source over the same origin at once.
        var origin = Pasted("hello");

        var outgoing = origin.Open();
        var incoming = origin.Open();

        Assert.NotSame(outgoing, incoming);
        Assert.Equal("hello", incoming.GetUtf8String(0, 5));
        outgoing.Release();
        Assert.Equal("hello", incoming.GetUtf8String(0, 5));
    }

    // ---- loading pasted bytes ----------------------------------------------------------

    [Fact]
    public async Task JsonLoadsFromPastedBytes()
    {
        var origin = Pasted("{\"name\":\"argo\",\"items\":[1,2,3]}");
        using var vm = new JsonViewModel();

        await vm.LoadAsync(origin);
        await vm.IndexingTask;

        Assert.Null(vm.IndexFailure);
        Assert.Same(origin, vm.Origin);
        Assert.Equal("Pasted text", vm.FilePath);
        Assert.True(vm.TokenCount > 0);
    }

    [Fact]
    public async Task NdJsonLoadsFromPastedBytes_AndItsPerLineJsonViewReadsTheSameOrigin()
    {
        var origin = Pasted("{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n");
        using var vm = new NdJsonViewModel();

        await vm.LoadAsync(origin);
        await vm.IndexingTask;

        Assert.Equal(3, vm.LineCount);

        // The nested per-line document is a sub-range of the same origin, so it needs no path.
        vm.LoadSelectedLine(1);
        var line = await WaitForLineDocumentAsync(vm);

        Assert.Same(origin, line.Origin);
        await line.IndexingTask;
        Assert.Null(line.IndexFailure);
    }

    private static async Task<JsonViewModel> WaitForLineDocumentAsync(NdJsonViewModel vm)
    {
        for (int i = 0; i < 200 && vm.SelectedLineJsonViewModel is null; i++)
            await Task.Delay(10);

        Assert.NotNull(vm.SelectedLineJsonViewModel);
        return vm.SelectedLineJsonViewModel!;
    }

    // ---- searching pasted bytes --------------------------------------------------------

    [Fact]
    public async Task SearchFindsMatchesInPastedBytes()
    {
        var origin = Pasted("alpha beta gamma beta delta");
        var session = SearchSession.Start(new ScanTarget(origin), new LiteralSearchMatcher("beta"));

        await session.ScanTask;

        Assert.Null(session.OpenFailure);
        Assert.Equal(2, session.MatchCount);
        Assert.Equal(6, session.GetMatch(0).Offset);
        Assert.Equal(17, session.GetMatch(1).Offset);
    }

    [Fact]
    public async Task SearchScansASubRangeOfAPasteTheSameWayItDoesAFile()
    {
        var origin = Pasted("beta|alpha beta");
        var session = SearchSession.Start(
            new ScanTarget(origin, Offset: 5, Length: 10),
            new LiteralSearchMatcher("beta"));

        await session.ScanTask;

        // Offsets are relative to the scanned range, as they are for a file sub-range.
        Assert.Equal(1, session.MatchCount);
        Assert.Equal(6, session.GetMatch(0).Offset);
    }

    [Fact]
    public async Task ASearchOutlivesTheDocumentThatStartedIt()
    {
        // The lifetime rule that forces search to take an origin rather than a source: the
        // document's session releases what it owns, and the scan must be unaffected.
        var origin = Pasted(new string('x', 4096) + "needle");
        var documentSource = origin.Open();

        var session = SearchSession.Start(new ScanTarget(origin), new LiteralSearchMatcher("needle"));
        documentSource.Release();
        await session.ScanTask;

        Assert.Null(session.OpenFailure);
        Assert.Equal(1, session.MatchCount);
    }

    // ---- degradation with no path ------------------------------------------------------

    [Fact]
    public void TheSchemaCatalogOffersUserSchemasButPreselectsNothingWithoutAPath()
    {
        var (entries, preselected, rootName) = JsonSchemaCatalog.GatherForDocument(null);

        Assert.NotNull(entries);
        Assert.Null(preselected);
        Assert.Null(rootName);
    }
}
