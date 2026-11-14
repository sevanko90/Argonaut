using System.Text;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>
/// Covers the catalog-wide matcher: that it finds the schema the rule picks, and that each schema
/// is parsed once per change rather than once per document opened.
/// </summary>
public sealed class CachedSchemaMatcherTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> loads = [];
    private readonly CachedSchemaMatcher matcher;

    private static readonly string[] Order = ["id", "customer", "lines", "total", "currency", "placedAt", "status", "notes"];

    public CachedSchemaMatcherTests()
    {
        Directory.CreateDirectory(folder);
        matcher = new CachedSchemaMatcher(path =>
        {
            lock (loads)
                loads.Add(path);
            return JsonSchemaLoader.TryLoadFile(path);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static IReadOnlyList<byte[]> Keys(params string[] names) => names.Select(Encoding.UTF8.GetBytes).ToArray();

    private static string Schema(params string[] names)
        => $$"""{ "properties": { {{string.Join(", ", names.Select(n => $"\"{n}\": {{}}"))}} } }""";

    private SchemaCatalogEntry Write(string name, string content)
    {
        string path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, content);
        return new SchemaCatalogEntry(name, path, IsUser: true);
    }

    [Fact]
    public async Task FindsTheSchemaThatFits()
    {
        var other = Write("address", Schema("line1", "city", "postcode"));
        var order = Write("order", Schema(Order));

        var match = await matcher.FitAsync([other, order], Keys(Order), CancellationToken.None);

        Assert.Equal(order, match.AutoMatch?.Entry);
        Assert.Null(match.AutoMatch?.RootName);
    }

    [Fact]
    public async Task NothingFitting_IsNull()
    {
        var order = Write("order", Schema(Order));

        var fit = await matcher.FitAsync([order], Keys("a", "b", "c"), CancellationToken.None);

        Assert.Null(fit.AutoMatch);
        Assert.Empty(fit.Fits);
    }

    [Fact]
    public async Task ASchemaIsParsedOnceAcrossDocuments()
    {
        var order = Write("order", Schema(Order));

        await matcher.FitAsync([order], Keys(Order), CancellationToken.None);
        await matcher.FitAsync([order], Keys("x", "y"), CancellationToken.None);

        Assert.Single(loads);
    }

    [Fact]
    public async Task AChangedSchemaIsParsedAgain()
    {
        var order = Write("order", Schema("a", "b"));
        await matcher.FitAsync([order], Keys(Order), CancellationToken.None);

        File.WriteAllText(order.FilePath, Schema(Order));
        File.SetLastWriteTimeUtc(order.FilePath, DateTime.UtcNow.AddMinutes(1));

        var match = await matcher.FitAsync([order], Keys(Order), CancellationToken.None);

        Assert.Equal(2, loads.Count);
        Assert.Equal(order, match.AutoMatch?.Entry);
    }

    [Fact]
    public async Task ABrokenOrMissingSchemaIsSkipped()
    {
        var broken = Write("broken", "{ not json");
        var missing = new SchemaCatalogEntry("missing", Path.Combine(folder, "missing.json"), IsUser: true);
        var order = Write("order", Schema(Order));

        var match = await matcher.FitAsync([broken, missing, order], Keys(Order), CancellationToken.None);

        Assert.Equal(order, match.AutoMatch?.Entry);
    }

    [Fact]
    public async Task ABrokenSchemaIsNotParsedAgainUntilItChanges()
    {
        var broken = Write("broken", "{ not json");

        await matcher.FitAsync([broken], Keys(Order), CancellationToken.None);
        await matcher.FitAsync([broken], Keys(Order), CancellationToken.None);

        Assert.Single(loads);
    }

    [Fact]
    public async Task AStopRequestedBeforehand_EndsTheSearch()
    {
        var order = Write("order", Schema(Order));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => matcher.FitAsync([order], Keys(Order), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Fits_ListPlausibleSchemasBestFirst_AndOnlyThose()
    {
        // Two schemas fitting equally - nothing is bound, and the name settles their order - one
        // fitting less well, and one fitting nothing.
        var orderA = Write("order-a", Schema(Order));
        var orderB = Write("order-b", Schema(Order));
        var partial = Write("partial", Schema([.. Order[..5], "p", "q", "r"]));
        var address = Write("address", Schema("line1", "city", "postcode"));

        var fit = await matcher.FitAsync([address, partial, orderB, orderA], Keys(Order), CancellationToken.None);

        Assert.Null(fit.AutoMatch);
        Assert.Equal([orderA, orderB, partial], fit.Fits.Select(f => f.Entry));
    }

    [Fact]
    public async Task Fits_LeadWithTheAutoMatch()
    {
        // "tiny" explains the whole two-key document but cannot be bound on two names; "pair"
        // requires both and so can - and leads, though "tiny" ties it on coverage.
        var tiny = Write("a-tiny", Schema("kind", "items", "x", "y"));
        var pair = Write("pair", """{ "properties": { "kind": {}, "items": {}, "z": {} }, "required": ["kind", "items"] }""");

        var fit = await matcher.FitAsync([tiny, pair], Keys("kind", "items"), CancellationToken.None);

        Assert.Equal(pair, fit.AutoMatch?.Entry);
        Assert.Equal(pair, fit.Fits[0].Entry);
    }
}
