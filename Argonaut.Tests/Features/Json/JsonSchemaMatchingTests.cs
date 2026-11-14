using Argonaut.Engine.Bytes;
using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// "Open matched schemas" through the JSON view model: a schema that clearly fits is bound when a
/// document with no binding opens, the schema list is ranked by fit either way, and a match never
/// replaces a choice - the user's this session, or the one they made last time, "No schema"
/// included. Harness rule (docs/headless-test-dispatch-hole.md): the dispatch body returns true
/// and the test returns the dispatch task.
/// </summary>
public sealed class JsonSchemaMatchingTests : IDisposable
{
    private static readonly string[] Order = ["id", "customer", "lines", "total", "currency", "placedAt", "status", "notes"];

    private readonly string root = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
    private readonly string documentPath;
    private readonly JsonSchemaCatalog catalog;
    private readonly string orderSchema;
    private readonly string addressSchema;

    public JsonSchemaMatchingTests()
    {
        string bundled = Path.Combine(root, "Bundled");
        string user = Path.Combine(root, "User");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        catalog = new JsonSchemaCatalog(bundled, user, revealDirectory: _ => { });

        orderSchema = Path.Combine(user, "order.json");
        File.WriteAllText(orderSchema, Schema(Order));
        addressSchema = Path.Combine(user, "address.json");
        File.WriteAllText(addressSchema, Schema("line1", "city", "postcode"));

        documentPath = Path.Combine(root, "document.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static string Schema(params string[] names)
        => $$"""{ "properties": { {{string.Join(", ", names.Select(n => $"\"{n}\": {{}}"))}} } }""";

    private static string Document(IEnumerable<string> names)
        => "{" + string.Join(", ", names.Select((n, i) => $"\"{n}\": {i}")) + "}";

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Gives a measurement that should bind nothing the time it would need to bind
    /// something, so a test asserting nothing happened is not just asserting too early.</summary>
    private static async Task SettleAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private Task WithView(string content, Func<JsonViewModel, SchemaBindings, Task> body,
        SchemaBindings? bindings = null, JsonViewSettings? settings = null)
    {
        File.WriteAllBytes(documentPath, Encoding.UTF8.GetBytes(content));
        bindings ??= new SchemaBindings();
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonSchemaMatchingTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new JsonViewModel(settings ?? new JsonViewSettings(), bindings, catalog, schemaMatcher: new CachedSchemaMatcher());
            try
            {
                await vm.LoadAsync(documentPath);
                await vm.IndexingTask;
                await body(vm, bindings);
            }
            finally
            {
                vm.Dispose();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task AFittingSchema_IsBoundAtOpen_AndNotRemembered() => WithView(Document(Order), async (vm, bindings) =>
    {
        await UntilAsync(() => vm.SchemaSettings.Document is not null);

        Assert.Equal(orderSchema, vm.SchemaSettings.SelectedEntry?.FilePath);
        Assert.True(vm.SchemaSettings.IsMatched);
        Assert.Empty(bindings.Entries);
    });

    [Fact]
    public Task TheSchemaList_IsRankedByFit() => WithView(Document(Order), async (vm, _) =>
    {
        await UntilAsync(() => vm.SchemaSettings.Fits.Count > 0);

        Assert.Equal([orderSchema], vm.SchemaSettings.Fits.Select(f => f.Entry.FilePath));
    });

    [Fact]
    public Task ARememberedBinding_Wins()
    {
        var bindings = new SchemaBindings();
        bindings.Remember(new FileLocator(documentPath).Identity, addressSchema);

        return WithView(Document(Order), async (vm, _) =>
        {
            await UntilAsync(() => vm.SchemaSettings.Fits.Count > 0);
            await SettleAsync();

            Assert.Equal(addressSchema, vm.SchemaSettings.SelectedEntry?.FilePath);
            Assert.False(vm.SchemaSettings.IsMatched);
        }, bindings);
    }

    [Fact]
    public Task NoSchemaChosenLastTime_IsNotOverriddenByAMatch()
    {
        var bindings = new SchemaBindings();
        bindings.Remember(new FileLocator(documentPath).Identity, null);

        return WithView(Document(Order), async (vm, _) =>
        {
            await UntilAsync(() => vm.SchemaSettings.Fits.Count > 0);
            await SettleAsync();

            Assert.Null(vm.SchemaSettings.SelectedEntry);
            Assert.Equal(orderSchema, vm.SchemaSettings.Fits[0].Entry.FilePath);
        }, bindings);
    }

    [Fact]
    public Task ClearingAMatch_RemembersNoSchema() => WithView(Document(Order), async (vm, bindings) =>
    {
        await UntilAsync(() => vm.SchemaSettings.Document is not null);

        await vm.SchemaSettings.SelectAsync(null);

        Assert.True(SchemaBindings.ChoseNoSchema(bindings.Entries, new FileLocator(documentPath).Identity));
    });

    [Fact]
    public async Task AChosenSchema_IsRemembered_ForTheSameDocumentNextTime()
    {
        var bindings = new SchemaBindings();
        var addressEntry = new SchemaCatalogEntry("address", addressSchema, IsUser: true);
        await WithView(Document(Order), async (vm, _) =>
        {
            await UntilAsync(() => vm.SchemaSettings.Document is not null);
            await vm.SchemaSettings.SelectAsync(addressEntry);
        }, bindings);

        await WithView(Document(Order), async (vm, _) =>
        {
            await UntilAsync(() => vm.SchemaSettings.SelectedEntry is not null);
            await SettleAsync();

            Assert.Equal(addressSchema, vm.SchemaSettings.SelectedEntry?.FilePath);
            Assert.False(vm.SchemaSettings.IsMatched);
        }, bindings);
    }

    [Fact]
    public Task WithTheOptionOff_NothingIsBound_ButTheListIsStillRanked() => WithView(Document(Order), async (vm, _) =>
    {
        await UntilAsync(() => vm.SchemaSettings.Fits.Count > 0);
        await SettleAsync();

        Assert.Null(vm.SchemaSettings.SelectedEntry);
        Assert.NotEmpty(vm.SchemaSettings.Fits);
    }, settings: new JsonViewSettings { OpenMatchedSchemas = false });

    [Fact]
    public Task AnArrayDocument_IsNotMatched() => WithView("[" + Document(Order) + "]", async (vm, _) =>
    {
        await SettleAsync();

        Assert.Null(vm.SchemaSettings.SelectedEntry);
        Assert.Empty(vm.SchemaSettings.Fits);
    });

    [Fact]
    public Task ADocumentFittingNothing_BindsNothing() => WithView(Document(["a", "b", "c"]), async (vm, _) =>
    {
        await SettleAsync();

        Assert.Null(vm.SchemaSettings.SelectedEntry);
        Assert.Empty(vm.SchemaSettings.Fits);
    });

    [Fact]
    public Task AnNdJsonFile_IsMatchedByItsFirstLine()
    {
        string ndjsonPath = Path.Combine(root, "document.ndjson");
        File.WriteAllText(ndjsonPath, Document(Order) + "\n" + Document(["something", "else"]) + "\n");
        var bindings = new SchemaBindings();

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonSchemaMatchingTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new Argonaut.Features.NdJson.NdJsonViewModel(new JsonViewSettings(), bindings, catalog, schemaMatcher: new CachedSchemaMatcher());
            try
            {
                await vm.LoadAsync(ndjsonPath);
                await vm.IndexingTask;
                await UntilAsync(() => vm.SchemaSettings.Document is not null);

                Assert.Equal(orderSchema, vm.SchemaSettings.SelectedEntry?.FilePath);
                Assert.True(vm.SchemaSettings.IsMatched);
                Assert.Empty(bindings.Entries);
            }
            finally
            {
                vm.Dispose();
            }

            return true;
        }, CancellationToken.None);
    }
}
