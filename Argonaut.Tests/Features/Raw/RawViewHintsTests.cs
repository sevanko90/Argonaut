using System.Text;
using Argonaut.Engine.Hints;
using Argonaut.Features.Json.Preview;
using Argonaut.Features.Raw;
using Argonaut.Features.Raw.Hints;
using Argonaut.Tests.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Raw;

/// <summary>
/// Value hints in the raw view itself: found on the rows the surface lays out once a lexer
/// colours them, chips that act when clicked, and nothing at all without a lexer.
/// </summary>
public sealed class RawViewHintsTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));

    public RawViewHintsTests() => Directory.CreateDirectory(tempDir);

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static string Jwt()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"HS256"}""")}.{Part("""{"sub":"u1"}""")}.c2ln";
    }

    private static async Task PumpAsync(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private Task WithView(string json, Func<Window, RawViewModel, RawTextSurface, Task> body)
        => WithView("doc.json", json, body);

    private Task WithView(string name, string json, Func<Window, RawViewModel, RawTextSurface, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewHintsTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = Path.Combine(tempDir, name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            var vm = new RawViewModel(new RawViewSettings());
            Window? window = null;
            try
            {
                await vm.LoadAsync(path);
                await vm.IndexingTask;

                window = new Window { Width = 1800, Height = 600, Content = new RawView { DataContext = vm } };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                await body(window, vm, window.GetVisualDescendants().OfType<RawTextSurface>().First());
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task APrettyPrintedFile_HasAChipPerHintedMember() => WithView("{\n  \"colour\": \"#336699\",\n  \"name\": \"plain\"\n}\n", async (_, _, surface) =>
    {
        var mark = Assert.Single(surface.HintMarksFor(1));
        Assert.True(mark.IsChip);
        Assert.Equal(ValueHintKind.Colour, mark.Hint.Kind);
        Assert.Empty(surface.HintMarksFor(2));
        Assert.NotNull(surface.HintChipBounds(1));
        await Task.CompletedTask;
    });

    [Fact]
    public Task WithoutALexer_ValuesAreReadAsWords_MarkedInPlace() => WithView("{\n  \"colour\": \"#336699\"\n}\n", async (_, _, surface) =>
    {
        surface.Lexer = null;
        var mark = Assert.Single(surface.HintMarksFor(1));
        Assert.False(mark.IsChip);
        Assert.Equal(ValueHintKind.Colour, mark.Hint.Kind);
        await Task.CompletedTask;
    });

    [Fact]
    public Task AMinifiedLine_MarksItsValuesInPlace() => WithView("{\"a\":\"#336699\",\"b\":\"https://example.com/x\"}\n", async (_, _, surface) =>
    {
        var marks = surface.HintMarksFor(0);
        Assert.Equal(2, marks.Count);
        Assert.All(marks, m => Assert.False(m.IsChip));
        Assert.Null(surface.HintChipBounds(0));
        await Task.CompletedTask;
    });

    [Fact]
    public Task ClickingAJwtChip_OpensTheCardWithItsClaims() => WithView($"{{\n  \"token\": \"{Jwt()}\"\n}}\n", async (window, _, surface) =>
    {
        RawHintHit? clicked = null;
        surface.HintClicked += (_, hit) => clicked = hit;

        var chip = surface.HintChipBounds(1)!.Value;
        var point = surface.TranslatePoint(chip.Center, window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        var card = window.GetVisualDescendants().OfType<HintCard>().Single();
        for (int i = 0; i < 50 && !card.IsOpen; i++)
            await PumpAsync();

        Assert.Equal(ValueHintKind.Jwt, clicked?.Value.Hint?.Kind);
        Assert.True(card.IsOpen);
        Assert.Equal("JWT · header and claims", card.ShownPreview?.Title);
        card.Close();
        await PumpAsync();
    });

    /// <summary>A plain click on a marked value places the caret like any text; it takes the
    /// command modifier to follow the hint.</summary>
    [Fact]
    public Task APlainClickOnAMarkedValue_PlacesTheCaret() => WithView("{\"a\":\"#336699\",\"b\":\"https://example.com/x\"}\n", async (window, vm, surface) =>
    {
        RawHintHit? clicked = null;
        surface.HintClicked += (_, hit) => clicked = hit;

        var mark = surface.HintMarksFor(0)[1];
        var bounds = surface.HintMarkBounds(0, mark)!.Value;
        var point = surface.TranslatePoint(bounds.Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await PumpAsync();
        Assert.Null(clicked);

        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Meta);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Meta);
        await PumpAsync();
        Assert.Equal(ValueHintKind.Url, clicked?.Value.Hint?.Kind);
    });

    [Fact]
    public Task AnEdit_MovesTheHintsWithTheText() => WithView("{\n  \"colour\": \"#336699\"\n}\n", async (_, vm, surface) =>
    {
        Assert.Single(surface.HintMarksFor(1));
        vm.SetEditing(true);
        vm.Caret!.PlaceAt(0);
        Assert.True(vm.TypeText("\n"));
        await PumpAsync();

        Assert.Empty(surface.HintMarksFor(1));
        Assert.Equal(ValueHintKind.Colour, Assert.Single(surface.HintMarksFor(2)).Hint.Kind);
    });

    [Fact]
    public Task AFileNoLexerReads_HintsItsUnambiguousWords() => WithView("notes.txt", "deploy log\nsee https://example.com/runs/42 at 2026-10-08T09:12:03Z, id 1709305509\n", async (_, _, surface) =>
    {
        Assert.Null(surface.Lexer);
        var marks = surface.HintMarksFor(1);
        Assert.Equal([ValueHintKind.Url, ValueHintKind.IsoDate], marks.Select(m => m.Hint.Kind));
        Assert.All(marks, m => Assert.False(m.IsChip));
        await Task.CompletedTask;
    });

    [Fact]
    public Task AWordWrappedOverRows_IsMarkedOnEachRow() => WithView("notes.txt", "head line\nsee https://example.com/" + new string('a', 400) + " done\n", async (_, _, surface) =>
    {
        Assert.Equal(ValueHintKind.Url, Assert.Single(surface.HintMarksFor(1)).Hint.Kind);
        Assert.Equal(ValueHintKind.Url, Assert.Single(surface.HintMarksFor(2)).Hint.Kind);
        Assert.Equal(ValueHintKind.Url, Assert.Single(surface.HintMarksFor(3)).Hint.Kind);
        await Task.CompletedTask;
    });

    [Fact]
    public Task HoveringAMarkedWord_ShowsWhatItReadsAs() => WithView("notes.txt", "head line\nsee https://example.com/a for more\n", async (window, _, surface) =>
    {
        var mark = Assert.Single(surface.HintMarksFor(1));
        var point = surface.TranslatePoint(surface.HintMarkBounds(1, mark)!.Value.Center, window)!.Value;
        window.MouseMove(point);
        await PumpAsync();

        Assert.Equal(mark.Value, surface.HintLayer.Hovered?.Value);
        Assert.Contains("Open example.com", ToolTip.GetTip(surface) as string);
    });
}
