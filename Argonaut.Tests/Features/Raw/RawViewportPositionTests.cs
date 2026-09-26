using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw;
using Argonaut.Tests.Support;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests.Features.Raw;

/// <summary>
/// Headless: the position the text view hands to the next view follows scrolling. Scrolling
/// moves the viewport and never the caret, so "where the user is" needs the surface to say what
/// it is showing - this is the check that it does.
/// </summary>
public sealed class RawViewportPositionTests
{
    private static async Task PumpAsync(int milliseconds = 50)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task ScrollingAwayFromTheCaret_MovesThePositionToTheViewport()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewportPositionTests).Assembly);
        return session.Dispatch(async () =>
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 5000; i++)
                sb.Append($"line {i:D5}\n");
            string path = Path.GetTempFileName();
            File.WriteAllText(path, sb.ToString());

            var vm = new RawViewModel(new RawViewSettings());
            Window? window = null;
            try
            {
                await vm.LoadAsync(path);
                await vm.IndexingTask;

                window = new Window { Width = 900, Height = 600, Content = new RawView { DataContext = vm } };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(ByteRange.At(0), vm.SelectedByteRange); // caret at the top, on screen

                vm.SelectRow(4000); // a scroll only - the caret stays on row 0
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(0, vm.Caret!.Caret.Offset);
                var position = vm.SelectedByteRange!.Value;
                Assert.Equal(0, position.Length);
                long lineStart = position.Offset;
                Assert.Equal(0, lineStart % 11); // "line NNNNN\n" is 11 bytes: a line start
                Assert.InRange(lineStart / 11, 3950, 4000);
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
