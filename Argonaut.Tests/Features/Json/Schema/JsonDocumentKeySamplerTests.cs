using System.Text;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>
/// Covers reading the outermost object's property names - the evidence
/// <see cref="JsonSchemaRootMatcher"/> scores schema types against.
/// </summary>
public class JsonDocumentKeySamplerTests
{
    private static Task WithIndexedAsync(string json, Action<JsonTreeHarness, JsonTreeHarness> body)
    {
        var tree = new JsonTreeHarness(json);
        body(tree, tree);
        return Task.CompletedTask;
    }

    private static IReadOnlyList<byte[]> RootKeys(JsonTreeHarness tree, out bool fromArray)
        => JsonDocumentKeySampler.ReadRootKeys(tree.Reader, tree.Text, out fromArray);

    private static IReadOnlyList<byte[]> MemberNames(JsonTreeHarness tree, string member)
        => JsonDocumentKeySampler.ReadMemberNames(tree.Reader, tree.Text, tree.Member(member).Row.Node);

    private static string[] Decode(IReadOnlyList<byte[]> keys)
        => keys.Select(k => Encoding.UTF8.GetString(k)).ToArray();

    [Fact]
    public Task ObjectRoot_ReadsItsDirectMemberNames()
        => WithIndexedAsync("""
            { "reference": "ABC", "passengers": [1, 2], "flight": { "number": "U28532" } }
            """,
            (index, file) =>
            {
                var keys = RootKeys(index, out bool fromArray);

                // Direct members only - `number` is a grandchild and must not appear.
                Assert.Equal(new[] { "reference", "passengers", "flight" }, Decode(keys));
                Assert.False(fromArray);
            });

    [Fact]
    public Task ArrayRoot_SamplesTheFirstElement()
        => WithIndexedAsync("""
            [ { "line1": "1 High St", "city": "Luton" }, { "line1": "2 Low St", "city": "Hove" } ]
            """,
            (index, file) =>
            {
                var keys = RootKeys(index, out bool fromArray);

                Assert.Equal(new[] { "line1", "city" }, Decode(keys));
                Assert.True(fromArray);
            });

    [Fact]
    public Task ArrayOfScalars_YieldsNothing()
        => WithIndexedAsync("[1, 2, 3]", (index, file) =>
        {
            Assert.Empty(RootKeys(index, out _));
        });

    [Fact]
    public Task ScalarRoot_YieldsNothing()
        => WithIndexedAsync("42", (index, file) =>
        {
            Assert.Empty(RootKeys(index, out _));
        });

    [Fact]
    public Task EmptyObject_YieldsNothing()
        => WithIndexedAsync("{}", (index, file) =>
        {
            Assert.Empty(RootKeys(index, out _));
        });

    [Fact]
    public Task KeyCount_IsCapped()
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < JsonDocumentKeySampler.MaxKeys * 3; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append($"\"k{i}\":{i}");
        }

        sb.Append('}');

        return WithIndexedAsync(sb.ToString(), (index, file) =>
        {
            // The cost of looking has to stay bounded, and past the cap nothing further
            // discriminates between candidate types.
            Assert.Equal(JsonDocumentKeySampler.MaxKeys, RootKeys(index, out _).Count);
        });
    }

    [Fact]
    public Task ReadMemberNames_ReadsAnyNamedContainer()
        => WithIndexedAsync("""
            { "data": { "line1": "1 High St", "city": "Luton" }, "meta": {} }
            """,
            (index, file) =>
            {
                // The wrapper root offers nothing to match on; the payload one level down does.
                // This is the entry point the per-node match affordance will use.
                Assert.Equal(new[] { "data", "meta" }, Decode(RootKeys(index, out _)));
                Assert.Equal(new[] { "line1", "city" }, Decode(MemberNames(index, "data")));
            });

    [Fact]
    public Task ReadMemberNames_OnANonObject_YieldsNothing()
        => WithIndexedAsync("""{ "items": [1, 2] }""", (index, file) =>
        {
            Assert.Empty(MemberNames(index, "items"));
        });
}
