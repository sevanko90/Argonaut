using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

/// <summary>
/// Find-term highlighting, and specifically what happens to a match cut in half by a soft wrap.
///
/// Highlighting re-matches the term against each row's displayed text, which is right for
/// everything except the boundary case: a row is only part of its line, so a term straddling the
/// break exists in neither row's text and used to light up in neither - while the caret and the
/// scroll went to it perfectly well, which is a confusing thing to watch.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawHighlightTests : IDisposable
{
    private readonly string tempDir;

    public RawHighlightTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        AppDataPaths.RootOverride = Path.Combine(tempDir, "settings");
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string content)
    {
        string path = Path.Combine(tempDir, "doc.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task PumpAsync(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Rebuilds the row's layout the way the surface does, so rectangles line up.</summary>
    private static TextLayout LayoutFor(RawTextSurface surface, RawViewModel vm, int rowIndex)
    {
        string text = ((RawVisibleRow)vm.Rows[rowIndex]!).Text;
        return new TextLayout(text, new Typeface(surface.FontFamily), surface.FontSize, Brushes.Black);
    }

    private Task WithView(string content, int wrapWidth, Func<RawViewModel, RawTextSurface, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawHighlightTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
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

                await body(vm, window.GetVisualDescendants().OfType<RawTextSurface>().First());
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
    public Task AMatchWithinOneRowIsHighlighted()
        => WithView(new string('a', 40) + "needle" + new string('b', 20) + "\n", 80, async (vm, surface) =>
        {
            vm.HighlightTerm = "needle";
            await PumpAsync();

            Assert.NotEmpty(surface.HighlightRectsFor(0, LayoutFor(surface, vm, 0)));
        });

    /// <summary>
    /// The reported case: the term is split by a forced break, so half of it is on each row.
    /// Both halves must light up, each in its own row.
    /// </summary>
    [Fact]
    public Task AMatchStraddlingASoftWrapIsHighlightedOnBothRows()
        => WithView(new string('a', 77) + "needle" + new string('b', 40) + "\n", 80, async (vm, surface) =>
        {
            // Wrap at 80 puts "nee" at the end of row 0 and "dle" at the start of row 1.
            Assert.True(vm.RowCount >= 2);
            Assert.True(((RawVisibleRow)vm.Rows[0]!).IsSoftWrapped, "row 0 should be force-broken");

            vm.HighlightTerm = "needle";
            await PumpAsync();

            var first = surface.HighlightRectsFor(0, LayoutFor(surface, vm, 0));
            var second = surface.HighlightRectsFor(1, LayoutFor(surface, vm, 1));

            Assert.NotEmpty(first);
            Assert.NotEmpty(second);

            // The first row's highlight runs to the end of its text, the second starts at the
            // beginning of its own - between them they cover the whole term.
            Assert.True(second[0].Left < 1.0,
                $"the continuation half should start at the left edge of its row, was at {second[0].Left:F1}");
        });

    /// <summary>
    /// The reach must not cross a real line ending. A term formed only by running the end of one
    /// line into the start of the next is not a match, and highlighting it would be inventing one.
    /// </summary>
    [Fact]
    public Task TextSpanningARealLineEndingIsNotHighlighted()
        => WithView("aaaaneed\nle bbbb\n", 80, async (vm, surface) =>
        {
            Assert.False(((RawVisibleRow)vm.Rows[0]!).IsSoftWrapped, "row 0 ends at a real newline");

            vm.HighlightTerm = "needle";
            await PumpAsync();

            Assert.Empty(surface.HighlightRectsFor(0, LayoutFor(surface, vm, 0)));
            Assert.Empty(surface.HighlightRectsFor(1, LayoutFor(surface, vm, 1)));
        });

    [Fact]
    public Task NoTermHighlightsNothing()
        => WithView(new string('a', 40) + "needle\n", 80, async (vm, surface) =>
        {
            vm.HighlightTerm = null;
            await PumpAsync();

            Assert.Empty(surface.HighlightRectsFor(0, LayoutFor(surface, vm, 0)));
        });
}
