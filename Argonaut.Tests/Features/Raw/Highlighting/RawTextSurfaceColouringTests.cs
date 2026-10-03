using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Tests.Support;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Raw.Highlighting;

/// <summary>
/// What the raw surface colours: the spans it lexes for each row, including the rows of a
/// wrapped line that begin mid-token, the bounded lookback that finds their state, and that an
/// edit re-colours the rows below it.
/// </summary>
public sealed class RawTextSurfaceColouringTests : IDisposable
{
    private static readonly JsonRawLexer Json = new();

    private readonly string tempDir;

    public RawTextSurfaceColouringTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string content)
    {
        string path = Path.Combine(tempDir, "doc.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task PumpAsync(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static Dictionary<RawTextStyle, IBrush> AllBrushes()
        => Enum.GetValues<RawTextStyle>().ToDictionary(style => style, _ => (IBrush)Brushes.Red);

    private Task WithView(string content, int wrapWidth, Func<RawViewModel, RawTextSurface, Task> body, bool colour = true)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawTextSurfaceColouringTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel(new RawViewSettings());
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteFile(content));
                await vm.IndexingTask;
                vm.SetWrapWidth(wrapWidth);
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 900, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var surface = window.GetVisualDescendants().OfType<RawTextSurface>().First();
                if (colour)
                {
                    surface.StyleBrushes = AllBrushes();
                    surface.Lexer = Json;
                }
                else
                {
                    // The view chooses a lexer from the file's name; this is the surface with none.
                    surface.Lexer = null;
                }

                await body(vm, surface);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }

    private static string TextOf(RawViewModel vm, int rowIndex) => ((RawVisibleRow)vm.Rows[rowIndex]!).Text;

    [Fact]
    public Task ShortDocument_RowsCarryTheLexersSpans()
        => WithView("{\"a\": 1}\n[true, null]\n", 80, (vm, surface) =>
        {
            for (int row = 0; row < 2; row++)
            {
                var expected = new List<RawStyledSpan>();
                Json.Lex(TextOf(vm, row), RawLexState.LineStart, expected);

                Assert.NotEmpty(expected);
                Assert.Equal(expected, surface.StyledSpansFor(row));
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task WrappedRowStartingInsideAString_StartsString()
        => WithView("[\"" + new string('x', 500) + "\", 1]\n", 80, (vm, surface) =>
        {
            Assert.True(vm.RowCount > 3);

            // Asked cold, before any row above it has been lexed: the surface finds the state
            // by lexing the line forward from its start.
            var spans = surface.StyledSpansFor(2);
            Assert.Equal(new RawStyledSpan(0, TextOf(vm, 2).Length, RawTextStyle.String), spans[0]);
            return Task.CompletedTask;
        });

    [Fact]
    public Task LineStartBeyondTheLookback_LeavesTheRestOfTheLinePlain_AndTheNextLineIsColouredAgain()
        => WithView("\"" + new string('x', 80 * 20) + "\"\n{\"k\": 1}\n", 80, (vm, surface) =>
        {
            int nextLine = Enumerable.Range(1, vm.RowCount - 1)
                .First(row => ((RawVisibleRow)vm.Rows[row]!).LineNumber == 2);
            Assert.True(nextLine > 18, $"the first line should span more than 18 rows, spanned {nextLine}");

            Assert.NotEmpty(surface.StyledSpansFor(16));  // the line start is 16 rows back: in reach
            Assert.Empty(surface.StyledSpansFor(18));     // 18 back: out of reach
            Assert.Empty(surface.StyledSpansFor(nextLine - 1)); // and the rest of the line stays plain

            Assert.Contains(surface.StyledSpansFor(nextLine), span => span.Style == RawTextStyle.Key);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Editing_RecoloursTheFollowingRowsOfTheLine()
        => WithView("[\"" + new string('a', 300) + "\", 1]\n", 80, async (vm, surface) =>
        {
            Assert.Equal(RawTextStyle.String, surface.StyledSpansFor(1)[0].Style);

            vm.SetEditing(true);
            Assert.True(vm.IsEditing);
            vm.Caret.PlaceAt(5);
            vm.TypeText("\"");
            await PumpAsync();

            // The quote closes the string early, so row 1 now begins in the bare word after it.
            var spans = surface.StyledSpansFor(1);
            Assert.True(spans.Count == 0 || spans[0].Start > 0 || spans[0].Style != RawTextStyle.String,
                "row 1 should no longer begin inside the string");

            vm.DeleteBackward();
            await PumpAsync();

            Assert.Equal(new RawStyledSpan(0, TextOf(vm, 1).Length, RawTextStyle.String), surface.StyledSpansFor(1)[0]);
        });

    [Fact]
    public Task WithNoLexer_NothingIsColoured()
        => WithView("{\"a\": 1}\n", 80, (vm, surface) =>
        {
            Assert.NotEmpty(surface.StyledSpansFor(0));

            surface.Lexer = null;
            Assert.Empty(surface.StyledSpansFor(0));
            Assert.Equal(0, surface.CachedLayoutCount);
            return Task.CompletedTask;
        });

    [Fact]
    public Task WithNoLexerFromTheStart_RowsStillLayOut()
        => WithView("{\"a\": 1}\n", 80, async (vm, surface) =>
        {
            Assert.Empty(surface.StyledSpansFor(0));
            surface.InvalidateMeasure();
            surface.InvalidateVisual();
            await PumpAsync();
            Assert.Equal(vm.RowCount, surface.RealizedRowCount);
        }, colour: false);

    [Fact]
    public void SectionHeaders_AreTheOneBoldStyle()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawTextSurfaceColouringTests).Assembly);
        session.Dispatch(() =>
        {
            var surface = new RawTextSurface { StyleBrushes = AllBrushes() };
            var typeface = new Typeface("Inter");

            foreach (var style in Enum.GetValues<RawTextStyle>())
            {
                var weight = surface.PropertiesFor(style, typeface, 13, Brushes.Black).Typeface.Weight;
                Assert.Equal(style == RawTextStyle.Section ? FontWeight.Bold : FontWeight.Normal, weight);
            }

            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
