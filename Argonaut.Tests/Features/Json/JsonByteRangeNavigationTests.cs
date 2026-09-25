using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;
using Argonaut.Ui.Documents.Navigation;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// The JSON view's side of the hop to and from the text view: which bytes the selected node
/// covers, and which node a byte offset selects. Assertions compare the text of the range
/// rather than offsets, so an off-by-one on a quote or bracket reads as the wrong text.
///
/// The static RawJumpService event is safe to use here because this assembly disables test
/// parallelization (see AssemblyInfo.cs); the subscription is still removed in a finally.
/// </summary>
public class JsonByteRangeNavigationTests
{
    private const string Json = "{\"name\": \"hi\", \"list\": [1, 2], \"n\": 42, \"empty\": {}}";

    private static async Task WithDocumentAsync(string json, Func<JsonViewModel, Task> test)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
        try
        {
            await vm.LoadAsync(path);
            await vm.IndexingTask;
            await test(vm);
        }
        finally
        {
            vm.Dispose();
            File.Delete(path);
        }
    }

    private static string TextOf(ByteRange? range)
    {
        Assert.NotNull(range);
        return Json.Substring((int)range.Value.Offset, (int)range.Value.Length);
    }

    private static int At(string fragment) => Json.IndexOf(fragment, StringComparison.Ordinal);

    [Theory]
    [InlineData("\"hi\"", "\"hi\"")] // a string keeps its quotes
    [InlineData("[1, 2]", "[1, 2]")] // a container runs bracket to bracket
    [InlineData("42", "42")]
    [InlineData("{}", "{}")] // an empty container
    public async Task Reveal_AtTheStartOfANode_SelectsIt_AndItsRangeIsTheNode(string revealAt, string expected)
    {
        await WithDocumentAsync(Json, async vm =>
        {
            await vm.RevealByteRangeAsync(ByteRange.At(At(revealAt)));

            Assert.Equal(expected, TextOf(vm.SelectedByteRange));
        });
    }

    [Fact]
    public async Task Reveal_OnAClosingBracket_SelectsTheContainerItCloses()
    {
        await WithDocumentAsync(Json, async vm =>
        {
            await vm.RevealByteRangeAsync(ByteRange.At(At("]")));

            Assert.Equal("[1, 2]", TextOf(vm.SelectedByteRange));
        });
    }

    [Fact]
    public async Task Reveal_OnTheRootsClosingBrace_SelectsTheWholeDocument()
    {
        await WithDocumentAsync(Json, async vm =>
        {
            await vm.RevealByteRangeAsync(ByteRange.At(Json.Length - 1));

            Assert.Equal(0, vm.SelectedTokenIndex);
            Assert.Equal(Json, TextOf(vm.SelectedByteRange));
        });
    }

    /// <summary>A property name belongs to the value it names, which is what the text view's
    /// caret on a key means.</summary>
    [Fact]
    public async Task Reveal_OnAPropertyName_SelectsItsValue()
    {
        await WithDocumentAsync(Json, async vm =>
        {
            await vm.RevealByteRangeAsync(ByteRange.At(At("\"list\"") + 2));

            Assert.Equal("[1, 2]", TextOf(vm.SelectedByteRange));
            Assert.Equal("$.list", vm.SelectedPath);
        });
    }

    /// <summary>The hop there and back lands on the node it left from. Compared by range, not
    /// token: a closing bracket's row stands for its container, so it comes back as the opener.</summary>
    [Fact]
    public async Task SelectedRange_RevealedAgain_SelectsTheSameNode()
    {
        await WithDocumentAsync(Json, async vm =>
        {
            for (int token = 0; token < vm.TokenCount; token++)
            {
                vm.SelectToken(token);
                var range = vm.SelectedByteRange;

                vm.SelectToken(0);
                await vm.RevealByteRangeAsync(range!.Value);

                Assert.Equal(range, vm.SelectedByteRange);
            }
        });
    }

    [Fact]
    public async Task NoSelection_HasNoRange()
    {
        await WithDocumentAsync(Json, vm =>
        {
            Assert.Null(vm.SelectedByteRange);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ShowSelectionInText_AsksTheShellForTheNodesRange()
    {
        ByteRange? requested = null;
        void Capture(ByteRange range) => requested = range;

        RawJumpService.Requested += Capture;
        try
        {
            await WithDocumentAsync(Json, async vm =>
            {
                await vm.RevealByteRangeAsync(ByteRange.At(At("42")));
                vm.ShowSelectionInText();

                Assert.Equal("42", TextOf(requested));
            });
        }
        finally
        {
            RawJumpService.Requested -= Capture;
        }
    }
}
