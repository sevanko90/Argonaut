using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Csv;
using Argonaut.Tests.Support;
using Argonaut.Ui.TableGrid;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Csv;

/// <summary>
/// Headless tests for what find's next/prev does to the CSV grid. The case that matters is the
/// repeat reveal: a file with a single match (or one line holding them all) sends the user back
/// to the row that is already selected, and scrolling away must not make next/prev a no-op.
/// </summary>
public sealed class CsvRevealTests
{
    private static async Task PumpAsync(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static string WriteCsv(int rows)
    {
        var content = new StringBuilder("id,name\n");
        for (int i = 0; i < rows; i++)
            content.Append(i).Append(",row-").Append(i).Append('\n');

        string path = Path.Combine(Path.GetTempPath(), $"argonaut-reveal-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.ToString());
        return path;
    }

    private static TableSurface Grid(Window window)
        => window.GetVisualDescendants().OfType<TableSurface>().First();

    [Fact]
    public Task RevealingTheSameRowTwice_ScrollsBackToItAfterTheUserScrollsAway()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CsvRevealTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteCsv(5_000);
            var vm = new CsvViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(path, (byte)',');
                await vm.IndexingTask;

                var view = new CsvView { DataContext = vm };
                window = new Window { Width = 900, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                // First find hit: scrolls to the match deep in the file.
                vm.SelectRow(4_000, 1);
                await PumpAsync();
                window.UpdateLayout();

                double revealed = Grid(window).ScrollFraction;
                Assert.True(revealed > 0, "the first reveal should scroll to the match");
                Assert.Equal(4_000, Grid(window).SelectedIndex);

                // The user scrolls away, then presses next - which lands on the same single match.
                Grid(window).ScrollToFraction(0);
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(0, Grid(window).ScrollFraction);

                vm.SelectRow(4_000, 1);
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(revealed, Grid(window).ScrollFraction, 6);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Going back to a place rather than a selection - what a filter reload does when nothing
    /// selected was on screen - puts its row at the top of the grid and selects nothing; and the
    /// place the grid then reports is that same row, so a second reload stays put too.
    /// </summary>
    [Fact]
    public Task RevealingAPlace_PutsItsRowAtTheTop_WithNothingSelected()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CsvRevealTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteCsv(5_000);
            var vm = new CsvViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(path, (byte)',');
                await vm.IndexingTask;

                window = new Window { Width = 900, Height = 600, Content = new CsvView { DataContext = vm } };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                string text = File.ReadAllText(path);
                long rowStart = text.IndexOf("\n3000,", StringComparison.Ordinal) + 1;
                await vm.RevealByteRangeAsync(ByteRange.At(rowStart));
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(3_000, Grid(window).RowsOnScreen().First);
                Assert.Equal(-1, Grid(window).SelectedIndex);
                Assert.Equal(ByteRange.At(rowStart), vm.SelectedByteRange);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }

    /// <summary>The row the user selects in the grid is the one the view model reports as where
    /// the document is - what a filter reload or a view switch carries over.</summary>
    [Fact]
    public Task SelectingARowInTheGrid_IsWhereTheDocumentIs()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CsvRevealTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteCsv(50);
            var vm = new CsvViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(path, (byte)',');
                await vm.IndexingTask;

                window = new Window { Width = 900, Height = 600, Content = new CsvView { DataContext = vm } };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(ByteRange.At("id,name\n".Length), vm.SelectedByteRange); // nothing selected: the top row

                Grid(window).SelectedIndex = 3;
                var range = Assert.NotNull(vm.SelectedByteRange);
                Assert.Equal("3,row-3", Encoding.UTF8.GetString(File.ReadAllBytes(path), (int)range.Offset, (int)range.Length));

                // Nothing selected: the top row on screen, as a place rather than a selection.
                Grid(window).SelectedIndex = -1;
                Assert.Equal(ByteRange.At("id,name\n".Length), vm.SelectedByteRange);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
                File.Delete(path);
            }
        }, CancellationToken.None);
    }
}
