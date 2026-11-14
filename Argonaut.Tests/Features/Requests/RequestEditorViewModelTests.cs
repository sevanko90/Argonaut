using System.Text;
using Argonaut.Engine.Http;
using Argonaut.Features.Requests;

namespace Argonaut.Tests.Features.Requests;

/// <summary>
/// The request pane without its view: the URL and the argument list kept in step both ways, the
/// method, and building what Send sends - or saying why it cannot.
/// </summary>
public sealed class RequestEditorViewModelTests
{
    private static string[] Rows(RequestEditorViewModel editor) => editor.Arguments.Select(r => $"{r.Key}={r.Value}").ToArray();

    [Fact]
    public void TypingAUrl_FillsTheArguments()
    {
        var editor = new RequestEditorViewModel { UrlText = "https://x.test/items?page=2&q=caf%C3%A9" };

        Assert.Equal(["page=2", "q=café"], Rows(editor));
    }

    [Fact]
    public void EditingAnArgument_RewritesOnlyItInTheUrl()
    {
        var editor = new RequestEditorViewModel { UrlText = "https://x.test/?raw=a%2Cb&page=2#f" };

        editor.Arguments[1].Value = "3 & up";

        Assert.Equal("https://x.test/?raw=a%2Cb&page=3%20%26%20up#f", editor.UrlText);
        Assert.Equal(2, editor.Arguments.Count);
    }

    [Fact]
    public void AddingARow_ChangesNothingUntilItHasAKey()
    {
        var editor = new RequestEditorViewModel { UrlText = "https://x.test/?a=1" };

        var row = editor.AddArgument();
        Assert.Equal("https://x.test/?a=1", editor.UrlText);

        row.Key = "b";
        Assert.Equal("https://x.test/?a=1&b", editor.UrlText);

        row.Value = "2";
        Assert.Equal("https://x.test/?a=1&b=2", editor.UrlText);
        Assert.Same(row, editor.Arguments[1]);
    }

    [Fact]
    public void RemovingARow_TakesItOutOfTheUrl()
    {
        var editor = new RequestEditorViewModel { UrlText = "https://x.test/?a=1&b=2&c=3" };

        editor.RemoveArgument(editor.Arguments[1]);

        Assert.Equal("https://x.test/?a=1&c=3", editor.UrlText);
        Assert.Equal(["a=1", "c=3"], Rows(editor));
    }

    [Fact]
    public void EditingTheUrl_ReplacesTheRows_WithoutRewritingTheUrl()
    {
        var editor = new RequestEditorViewModel { UrlText = "https://x.test/?a=1" };
        int changes = 0;
        editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RequestEditorViewModel.UrlText)) changes++; };

        editor.UrlText = "https://x.test/?a=1&b=%7E";

        Assert.Equal(1, changes);
        Assert.Equal("https://x.test/?a=1&b=%7E", editor.UrlText);
        Assert.Equal(["a=1", "b=~"], Rows(editor));
    }

    [Fact]
    public void ABodyIsOfferedForPostOnly()
    {
        var editor = new RequestEditorViewModel();

        Assert.False(editor.HasBody);
        editor.Method = HttpRequestMethod.Post;
        Assert.True(editor.HasBody);
    }

    [Fact]
    public void BuildingARequest_TakesTheMethodUrlAndBody()
    {
        var editor = new RequestEditorViewModel { Method = HttpRequestMethod.Post, UrlText = "https://x.test/q?a=1" };

        var request = editor.BuildRequest(Encoding.UTF8.GetBytes("{}"), out string? problem);

        Assert.Null(problem);
        Assert.Equal(HttpRequestMethod.Post, request!.Method);
        Assert.Equal("https://x.test/q?a=1", request.Url.OriginalString);
        Assert.Equal("{}", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public void AUrlThatCannotBeSent_SaysWhy()
    {
        var editor = new RequestEditorViewModel { UrlText = "x.test/q" };

        Assert.Null(editor.BuildRequest(null, out string? problem));
        Assert.Contains("http://", problem);
    }

    [Fact]
    public async Task LoadingASavedRequest_FillsEverything()
    {
        HttpRequestSpec.TryCreate(HttpRequestMethod.Post, "https://x.test/q?a=1", Encoding.UTF8.GetBytes("{}"), out var request, out _);
        var editor = new RequestEditorViewModel();

        await editor.LoadAsync(request!);

        Assert.Equal(HttpRequestMethod.Post, editor.Method);
        Assert.Equal("https://x.test/q?a=1", editor.UrlText);
        Assert.Equal(["a=1"], Rows(editor));
    }

    [Fact]
    public async Task Send_HandsTheRequestToTheSender()
    {
        var sender = new RecordingSender();
        var editor = new RequestEditorViewModel(sender) { UrlText = "https://x.test/items?page=2" };

        await editor.SendAsync();

        Assert.Equal("https://x.test/items?page=2", Assert.Single(sender.Sent).Url.OriginalString);
        Assert.Null(editor.StatusText);
        Assert.False(editor.IsSending);
    }

    [Fact]
    public async Task Send_WithAUrlThatCannotBeSent_SaysWhy_AndSendsNothing()
    {
        var sender = new RecordingSender();
        var editor = new RequestEditorViewModel(sender) { UrlText = "x.test" };

        await editor.SendAsync();

        Assert.Empty(sender.Sent);
        Assert.Contains("http://", editor.StatusText);
    }

    [Fact]
    public async Task WhileSending_SendIsUnavailable()
    {
        var gate = new TaskCompletionSource();
        var sender = new RecordingSender { Gate = gate.Task };
        var editor = new RequestEditorViewModel(sender) { UrlText = "https://x.test/" };

        var sending = editor.SendAsync();
        Assert.True(editor.IsSending);
        Assert.False(editor.CanSend);
        await editor.SendAsync();

        gate.SetResult();
        await sending;
        Assert.Single(sender.Sent);
        Assert.True(editor.CanSend);
    }

    private sealed class RecordingSender : IRequestSender
    {
        public List<HttpRequestSpec> Sent { get; } = [];

        public Task Gate { get; init; } = Task.CompletedTask;

        public async Task SendAsync(HttpRequestSpec request)
        {
            Sent.Add(request);
            await Gate;
        }
    }
}
