using Argonaut.Shell;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests.Shell;

/// <summary>
/// Ctrl+V on the empty state opens the clipboard as a document - unless the user is typing. The
/// window's shortcut handler tunnels, so it sees the keystroke before a focused text box; the
/// rename box of a saved source is one such box (issue #49).
/// </summary>
public sealed class GlobalPasteFocusTests
{
    [Fact]
    public Task AFocusedTextBox_TakesThePaste_AndAButtonDoesNot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GlobalPasteFocusTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rename = new TextBox();
            var button = new Button { Content = "Open" };
            var window = new Window { Content = new StackPanel { Children = { button, rename } } };
            window.Show();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();

            rename.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(MainWindow.TakesTypedText(window.FocusManager?.GetFocusedElement()));

            button.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(MainWindow.TakesTypedText(window.FocusManager?.GetFocusedElement()));

            Assert.False(MainWindow.TakesTypedText(null));
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
