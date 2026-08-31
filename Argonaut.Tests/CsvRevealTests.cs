using System.Text;
using Argonaut.Features.Csv;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

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

    private static ScrollViewer BodyScroll(Window window)
        => window.GetVisualDescendants().OfType<TableView>().First()
            .GetVisualDescendants().OfType<ScrollViewer>().First();

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

                double revealed = BodyScroll(window).Offset.Y;
                Assert.True(revealed > 0, "the first reveal should scroll to the match");

                // The user scrolls away, then presses next - which lands on the same single match.
                BodyScroll(window).Offset = new Vector(0, 0);
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(0, BodyScroll(window).Offset.Y);

                vm.SelectRow(4_000, 1);
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(revealed, BodyScroll(window).Offset.Y, 1);
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
