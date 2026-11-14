using System.ComponentModel;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;

namespace Argonaut.Tests.Ui.Documents;

/// <summary>
/// A document shown inside another view rather than as the window's document: published only
/// once loaded, the latest load winning, and everything it loaded released.
/// </summary>
public sealed class HostedDocumentTests
{
    [Fact]
    public async Task ADocument_IsPublishedOnlyOnceLoaded()
    {
        var gate = new TaskCompletionSource();
        var loaded = new Doc();
        using var host = new HostedDocument(async (_, _) => { await gate.Task; return loaded; });

        var showing = host.ShowAsync(FileTypeDetector.FileKind.Json, () => new MemoryByteOrigin([1], "x"));
        Assert.Null(host.Document);

        gate.SetResult();
        await showing;
        Assert.Same(loaded, host.Document);
    }

    [Fact]
    public async Task ShowingAnother_ReplacesAndReleasesTheFirst_WithItsOrigin()
    {
        var first = new Doc();
        var second = new Doc();
        var docs = new Queue<Doc>([first, second]);
        var origins = new List<TrackedOrigin>();
        using var host = new HostedDocument((_, _) => Task.FromResult<IDocumentViewModel>(docs.Dequeue()));

        await host.ShowAsync(FileTypeDetector.FileKind.Json, () => { var o = new TrackedOrigin(); origins.Add(o); return o; });
        await host.ShowAsync(FileTypeDetector.FileKind.Unidentified, () => { var o = new TrackedOrigin(); origins.Add(o); return o; });

        Assert.Same(second, host.Document);
        Assert.True(first.Disposed);
        Assert.True(origins[0].Disposed);
        Assert.False(origins[1].Disposed);
    }

    [Fact]
    public async Task ALoadOvertakenByAnother_IsThrownAway()
    {
        var slow = new TaskCompletionSource<IDocumentViewModel>();
        var stale = new Doc();
        var fresh = new Doc();
        int call = 0;
        using var host = new HostedDocument((_, _) => ++call == 1 ? slow.Task : Task.FromResult<IDocumentViewModel>(fresh));

        var first = host.ShowAsync(FileTypeDetector.FileKind.Json, () => new MemoryByteOrigin([1], "a"));
        await host.ShowAsync(FileTypeDetector.FileKind.Json, () => new MemoryByteOrigin([2], "b"));
        slow.SetResult(stale);
        await first;

        Assert.Same(fresh, host.Document);
        Assert.True(stale.Disposed);
    }

    [Fact]
    public async Task Disposing_ReleasesTheShownDocument_AndAnyLoadStillRunning()
    {
        var slow = new TaskCompletionSource<IDocumentViewModel>();
        var shown = new Doc();
        var late = new Doc();
        int call = 0;
        var host = new HostedDocument((_, _) => ++call == 1 ? Task.FromResult<IDocumentViewModel>(shown) : slow.Task);
        await host.ShowAsync(FileTypeDetector.FileKind.Json, () => new MemoryByteOrigin([1], "a"));
        var pending = host.ShowAsync(FileTypeDetector.FileKind.Json, () => new MemoryByteOrigin([2], "b"));

        host.Dispose();
        slow.SetResult(late);
        await pending;

        Assert.True(shown.Disposed);
        Assert.True(late.Disposed);
        Assert.Null(host.Document);
    }

    private sealed class Doc : IDocumentViewModel
    {
        public IByteOrigin? Origin => null;
        public string FilePath => string.Empty;
        public string StatusDetail => string.Empty;
        public object? Toolbar => null;
        public ISearchNavigator? CreateSearchNavigator() => null;
        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;
        public IndexFailure? IndexFailure => null;
        public Task IndexingTask => Task.CompletedTask;
        public bool Disposed { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Dispose() => Disposed = true;
    }

    private sealed class TrackedOrigin : IByteOrigin
    {
        public bool Disposed { get; private set; }
        public string DisplayName => "tracked";
        public SourceLocator? Locator => null;
        public long AvailableLength => 0;
        public IByteSource Open() => throw new NotSupportedException();
        public IByteSource OpenRange(long offset, long length) => throw new NotSupportedException();
        public KeptIndexes KeptIndexes { get; } = new();
        public void Dispose() => Disposed = true;
    }
}
