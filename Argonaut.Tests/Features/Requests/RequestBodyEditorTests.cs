using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Features.Requests;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests.Features.Requests;

/// <summary>The hosted raw editor as a POST body box: it starts editable, and what is typed is
/// what is sent. Harness rule (docs/headless-test-dispatch-hole.md): the dispatch body returns
/// true and the test returns the dispatch task.</summary>
public sealed class RequestBodyEditorTests
{
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task WhatIsTyped_IsTheBody()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RequestBodyEditorTests).Assembly);
        return session.Dispatch(async () =>
        {
            using var body = new RequestBodyEditor(new RawViewSettings());
            await body.LoadAsync(Encoding.UTF8.GetBytes("{}"));
            await UntilAsync(() => body.Editor!.IsEditing);

            Assert.True(body.Editor!.IsEditing);
            Assert.Equal("{}", Encoding.UTF8.GetString(body.ReadBody()));

            body.Editor.TypeText("x");

            Assert.Equal(3, body.ReadBody().Length);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task LoadingAgain_ReplacesTheEditor_AndReleasesTheOldOne()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RequestBodyEditorTests).Assembly);
        return session.Dispatch(async () =>
        {
            using var body = new RequestBodyEditor(new RawViewSettings());
            int changes = 0;
            body.EditorChanged += (_, _) => changes++;

            await body.LoadAsync(Encoding.UTF8.GetBytes("one"));
            var first = body.Editor!;
            await body.LoadAsync(Encoding.UTF8.GetBytes("second"));

            Assert.NotSame(first, body.Editor);
            Assert.Throws<ObjectDisposedException>(() => first.Document!.CopyTo(0, new byte[1]));
            Assert.Equal(2, changes);
            Assert.Equal("second", Encoding.UTF8.GetString(body.ReadBody()));
            return true;
        }, CancellationToken.None);
    }
}
