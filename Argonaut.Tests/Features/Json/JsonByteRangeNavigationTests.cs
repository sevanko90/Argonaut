using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// The JSON view's side of a switch to and from the text view: which bytes the selected node
/// covers, and which row a byte offset selects. Headless, because a reveal is resolved to a row by
/// the view's tree surface. Assertions compare the text of the range rather than offsets, so an
/// off-by-one on a quote or bracket reads as the wrong text. Harness rule
/// (docs/headless-test-dispatch-hole.md): the dispatch body returns true and the test returns the
/// dispatch task.
///
/// The static RawJumpService event is safe to use here because this assembly disables test
/// parallelization (see AssemblyInfo.cs); the subscription is still removed in a finally.
/// </summary>
public sealed class JsonByteRangeNavigationTests : IDisposable
{
    private const string Json = "{\"name\": \"hi\", \"list\": [1, 2], \"n\": 42, \"empty\": {}}";

    private readonly string path = Path.Combine(Path.GetTempPath(), $"range-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(path);

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs <paramref name="body"/> over <paramref name="content"/> loaded whole, or - with
    /// <paramref name="range"/> - over that byte range of it, the way NDJSON loads one line.</summary>
    private Task WithView(string content, Func<JsonViewModel, TreeSurface, Task> body, ByteRange? range = null)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonByteRangeNavigationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
            var window = new Window { Width = 900, Height = 500 };
            try
            {
                if (range is { } r)
                    await vm.LoadAsync(path, r.Offset, r.Length);
                else
                    await vm.LoadAsync(path);
                await vm.IndexingTask;
                window.Content = new JsonView { DataContext = vm };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                var surface = window.GetVisualDescendants().OfType<TreeSurface>().Single();
                await body(vm, surface);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private static async Task RevealAsync(JsonViewModel vm, ByteRange range)
    {
        await vm.RevealByteRangeAsync(range);
        await PumpAsync();
    }

    private static string TextOf(ByteRange? range, string content = Json)
    {
        Assert.NotNull(range);
        return content.Substring((int)range.Value.Offset, (int)range.Value.Length);
    }

    private static int At(string fragment) => Json.IndexOf(fragment, StringComparison.Ordinal);

    [Theory]
    [InlineData("\"hi\"", "\"hi\"")] // a string keeps its quotes
    [InlineData("[1, 2]", "[1, 2]")] // a container runs bracket to bracket
    [InlineData("42", "42")]
    [InlineData("{}", "{}")] // an empty container
    public Task Reveal_AtTheStartOfANode_SelectsIt_AndItsRangeIsTheNode(string revealAt, string expected)
        => WithView(Json, async (vm, _) =>
        {
            await RevealAsync(vm, ByteRange.At(At(revealAt)));

            Assert.Equal(expected, TextOf(vm.SelectedByteRange));
        });

    [Fact]
    public Task Reveal_OnAClosingBracket_SelectsTheContainerItCloses() => WithView(Json, async (vm, surface) =>
    {
        await RevealAsync(vm, ByteRange.At(At("]")));

        Assert.Equal(TreeRowShape.Close, surface.SelectedRow!.Value.Shape);
        Assert.Equal("[1, 2]", TextOf(vm.SelectedByteRange));
    });

    [Fact]
    public Task Reveal_OnTheRootsClosingBrace_SelectsTheWholeDocument() => WithView(Json, async (vm, _) =>
    {
        await RevealAsync(vm, ByteRange.At(Json.Length - 1));

        Assert.Equal(Json, TextOf(vm.SelectedByteRange));
    });

    /// <summary>A property name belongs to the value it names, which is what the text view's
    /// caret on a key means.</summary>
    [Fact]
    public Task Reveal_OnAPropertyName_SelectsItsValue() => WithView(Json, async (vm, _) =>
    {
        await RevealAsync(vm, ByteRange.At(At("\"list\"") + 2));

        Assert.Equal("[1, 2]", TextOf(vm.SelectedByteRange));
        Assert.Equal("$.list", vm.SelectedPath);
    });

    /// <summary>A switch there and back lands on the row it left from, for every row.</summary>
    [Fact]
    public Task SelectedRange_RevealedAgain_SelectsTheSameRow() => WithView(Json, async (vm, surface) =>
    {
        var rows = new List<TreeRow>();
        var cursor = vm.Tree!.NewCursor();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            rows.Add(cursor.Current);
        Assert.True(rows.Count > 5);

        foreach (var row in rows)
        {
            vm.OnRowSelected(row);
            var range = vm.SelectedByteRange;

            await RevealAsync(vm, ByteRange.At(0));
            await RevealAsync(vm, range!.Value);

            Assert.Equal(range, vm.SelectedByteRange);
        }
    });

    [Fact]
    public Task NoSelection_HasNoRange() => WithView(Json, (vm, _) =>
    {
        Assert.Null(vm.SelectedByteRange);
        return Task.CompletedTask;
    });

    /// <summary>An NDJSON line's tree reads its bytes from the line's start, but the range it
    /// reports and takes is in the file's offsets - the coordinate the text view shares.</summary>
    [Fact]
    public Task ALineOfALargerFile_SpeaksInFileOffsets()
    {
        string line = "{\"a\": [1, 2]}";
        string file = "{\"skip\": 0}\n" + line + "\n{\"after\": 1}\n";
        int lineStart = file.IndexOf(line, StringComparison.Ordinal);

        return WithView(file, async (vm, _) =>
        {
            await RevealAsync(vm, ByteRange.At(file.IndexOf('[', lineStart)));
            Assert.Equal("[1, 2]", TextOf(vm.SelectedByteRange, file));

            // Bytes of another line are not this document's to reveal.
            await RevealAsync(vm, ByteRange.At(file.IndexOf("after", StringComparison.Ordinal)));
            Assert.Equal("[1, 2]", TextOf(vm.SelectedByteRange, file));
        }, new ByteRange(lineStart, line.Length));
    }

    [Fact]
    public Task ShowSelectionInText_AsksTheShellForTheNodesRange()
    {
        ByteRange? requested = null;
        void Capture(ByteRange range) => requested = range;

        RawJumpService.Requested += Capture;
        return WithView(Json, async (vm, _) =>
        {
            try
            {
                await RevealAsync(vm, ByteRange.At(At("42")));
                vm.ShowSelectionInText();

                Assert.Equal("42", TextOf(requested));
            }
            finally
            {
                RawJumpService.Requested -= Capture;
            }
        });
    }

    /// <summary>A document whose bytes arrive as <see cref="GrowingByteSource"/> releases them.</summary>
    private sealed class GrowingOrigin(GrowingByteSource bytes) : IByteOrigin
    {
        private readonly MemoryByteOrigin kept = new([], "growing");

        public string DisplayName => "growing";
        public string? Path => null;
        public long AvailableLength => bytes.AvailableLength;
        public bool LengthSettled => bytes.LengthSettled;
        public KeptIndexes KeptIndexes => kept.KeptIndexes;
        public IByteSource Open() => bytes;
        public IByteSource OpenRange(long offset, long length) => throw new NotSupportedException();
        public void Dispose() => kept.Dispose();
    }

    /// <summary>
    /// A reveal past what the index covers waits for it, and until then there is nothing for a
    /// view to show - a view attached meanwhile (a switch back from the text view lays its view
    /// out at once) would otherwise seek past the index and land on the wrong row.
    /// </summary>
    [Fact]
    public async Task ARevealPastTheIndex_IsNotShownUntilTheIndexReachesIt()
    {
        var text = new StringBuilder("{\"features\":[\n");
        for (int i = 0; i < 2000; i++)
            text.Append(i == 0 ? "" : ",\n").Append($"{{\"id\":{i},\"coordinates\":[[{i},1],[{i},2]]}}");
        byte[] json = Encoding.UTF8.GetBytes(text.Append("\n]}").ToString());
        var growing = new GrowingByteSource(json, initiallyAvailable: json.Length / 4);
        var vm = new JsonViewModel(new JsonViewSettings(), new SchemaBindings(), TestSchemas.Catalog());
        try
        {
            await vm.LoadAsync(new GrowingOrigin(growing));
            long target = Encoding.UTF8.GetString(json).IndexOf("{\"id\":1500,", StringComparison.Ordinal);

            vm.Reveal(target);
            Assert.Null(vm.PendingReveal);

            growing.Seal();
            await vm.IndexingTask;
            for (int i = 0; i < 50 && vm.PendingReveal is null; i++)
                await Task.Delay(50);

            Assert.Equal(target, vm.PendingReveal);
        }
        finally
        {
            growing.Seal();
            vm.Dispose();
        }
    }
}
