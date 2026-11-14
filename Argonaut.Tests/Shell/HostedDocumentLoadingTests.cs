using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Settings;
using Argonaut.Features.Json;
using Argonaut.Features.Raw;
using Argonaut.Features.Xml;
using Argonaut.Shell;
using Argonaut.Tests.Support;
using Avalonia.Headless;

namespace Argonaut.Tests.Shell;

/// <summary>
/// How the catalog sets up a document hosted inside another view - a failed request's server
/// message: fully expanded, no table action, and raw for anything that is not JSON or XML.
/// Harness rule (docs/headless-test-dispatch-hole.md): the dispatch body returns true.
/// </summary>
public sealed class HostedDocumentLoadingTests
{
    private static DocumentViewCatalog Catalog() => new(SettingsStore.InMemory(), TestSchemas.Catalog());

    [Fact]
    public Task HostedJson_ExpandsFully_AndOffersNoTable()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HostedDocumentLoadingTests).Assembly);
        return session.Dispatch(async () =>
        {
            using var origin = new MemoryByteOrigin(Encoding.UTF8.GetBytes("""{"a":{"b":{"c":{"d":{"e":{"f":[1,2]}}}}}}"""), "Server message");
            using var vm = (JsonViewModel)await Catalog().LoadHostedAsync(FileTypeDetector.FileKind.Json, origin);

            Assert.True(vm.ExpandsFully);
            Assert.False(vm.SupportsArrayTable);
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(FileTypeDetector.FileKind.Xml, typeof(XmlViewModel))]
    [InlineData(FileTypeDetector.FileKind.Unidentified, typeof(RawViewModel))]
    public Task OtherKinds_GetTheirView(FileTypeDetector.FileKind kind, Type expected)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HostedDocumentLoadingTests).Assembly);
        return session.Dispatch(async () =>
        {
            using var origin = new MemoryByteOrigin(Encoding.UTF8.GetBytes("<error><m>no</m></error>"), "Server message");
            using var vm = await Catalog().LoadHostedAsync(kind, origin);

            Assert.IsType(expected, vm);
            return true;
        }, CancellationToken.None);
    }
}
