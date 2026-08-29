using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies IndexedDocumentViewModel in isolation, with a fake session - the dispose ordering
/// it exists to encode in one place (RequestStop -> rows -> DisposeCore -> session.Dispose),
/// and the monitor's two guards (disposed, and the session having moved on to a different
/// indexing task) that keep a stale completion from writing over a superseded or torn-down
/// document's state.
/// </summary>
public class IndexedDocumentViewModelTests
{
    private sealed class RecordingSession : IDocumentSession
    {
        private readonly List<string> order;
        public RecordingSession(List<string> order) => this.order = order;

        /// <summary>Settable so a test can stand in for RawIndexSession's wrap-width restart
        /// swapping the task out from under an in-flight monitor.</summary>
        public Task IndexingTask { get; set; } = Task.CompletedTask;

        public IndexFailure? Failure { get; set; }

        public CancellationToken TearingDown => CancellationToken.None;
        public void RequestStop() => order.Add("session.RequestStop");
        public void Dispose() => order.Add("session.Dispose");
    }

    private sealed class RecordingRows : IDisposable
    {
        private readonly List<string> order;
        public RecordingRows(List<string> order) => this.order = order;
        public void Dispose() => order.Add("rows.Dispose");
    }

    private sealed class TestDocument : IndexedDocumentViewModel
    {
        public List<string>? DisposeOrder;
        public bool CompletedWasCalled { get; private set; }
        public bool FailedWasCalled { get; private set; }
        public IndexFailure? FailedWith { get; private set; }

        public IDocumentSession? AttachedSession { get; set; }
        public IDisposable? AttachedRows { get; set; }

        protected override IDocumentSession? Session => AttachedSession;
        protected override IDisposable? MappedRows => AttachedRows;

        public override object? Toolbar => null;
        public override ISearchNavigator? CreateSearchNavigator() => null;
        public override bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

        public new void MonitorIndexing() => base.MonitorIndexing();

        public new bool IsDisposed => base.IsDisposed;

        protected override void DisposeCore() => DisposeOrder?.Add("disposeCore");

        protected override void OnIndexingCompleted() => CompletedWasCalled = true;

        protected override void OnIndexingFailed(IndexFailure? failure)
        {
            FailedWasCalled = true;
            FailedWith = failure;
        }
    }

    [Fact]
    public void IndexingTask_IsCompletedTask_BeforeASessionExists()
    {
        var doc = new TestDocument();
        Assert.True(doc.IndexingTask.IsCompleted);
    }

    [Fact]
    public void IndexingTask_TracksTheSessionsCurrentTask()
    {
        var order = new List<string>();
        var tcs = new TaskCompletionSource();
        var session = new RecordingSession(order) { IndexingTask = tcs.Task };
        var doc = new TestDocument { AttachedSession = session };

        Assert.Same(tcs.Task, doc.IndexingTask);

        // A restart swaps the session's task; the document must follow it, not the one it saw
        // first - this is what RawViewModel's wrap-width change relies on.
        var restarted = new TaskCompletionSource();
        session.IndexingTask = restarted.Task;

        Assert.Same(restarted.Task, doc.IndexingTask);
    }

    [Fact]
    public void Dispose_OrdersRequestStop_ThenRows_ThenDisposeCore_ThenSessionDispose()
    {
        var order = new List<string>();
        var doc = new TestDocument
        {
            DisposeOrder = order,
            AttachedSession = new RecordingSession(order),
            AttachedRows = new RecordingRows(order),
        };

        doc.Dispose();

        Assert.Equal(new[] { "session.RequestStop", "rows.Dispose", "disposeCore", "session.Dispose" }, order);
        Assert.True(doc.IsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var order = new List<string>();
        var doc = new TestDocument { DisposeOrder = order, AttachedSession = new RecordingSession(order) };

        doc.Dispose();
        doc.Dispose();

        Assert.Equal(new[] { "session.RequestStop", "disposeCore", "session.Dispose" }, order);
    }

    [Fact]
    public async Task MonitorIndexing_Completion_CallsOnIndexingCompleted()
    {
        var order = new List<string>();
        var tcs = new TaskCompletionSource();
        var doc = new TestDocument { AttachedSession = new RecordingSession(order) { IndexingTask = tcs.Task } };

        doc.MonitorIndexing();
        Assert.False(doc.CompletedWasCalled); // not yet - the task hasn't resolved

        tcs.SetResult();
        await tcs.Task; // let the continuation observe completion before asserting
        await Task.Yield();
        await Task.Yield();

        Assert.True(doc.CompletedWasCalled);
        Assert.False(doc.FailedWasCalled);
    }

    [Fact]
    public async Task MonitorIndexing_Cancellation_CallsOnIndexingFailedWithNull()
    {
        var order = new List<string>();
        var tcs = new TaskCompletionSource();
        var doc = new TestDocument { AttachedSession = new RecordingSession(order) { IndexingTask = tcs.Task } };

        doc.MonitorIndexing();
        tcs.SetCanceled();

        await WaitUntilAsync(() => doc.FailedWasCalled);

        Assert.True(doc.FailedWasCalled);
        Assert.Null(doc.FailedWith); // null means cancellation, distinct from a real failure
    }

    [Fact]
    public async Task MonitorIndexing_Failure_CallsOnIndexingFailedWithTheSessionsFailure()
    {
        var order = new List<string>();
        var tcs = new TaskCompletionSource();
        var failure = new IndexFailure("boom", 10, 1, 1, 5);
        var doc = new TestDocument
        {
            AttachedSession = new RecordingSession(order) { IndexingTask = tcs.Task, Failure = failure },
        };

        doc.MonitorIndexing();
        tcs.SetException(new InvalidOperationException("scan failed"));

        await WaitUntilAsync(() => doc.FailedWasCalled);

        Assert.Same(failure, doc.FailedWith);
    }

    [Fact]
    public async Task MonitorIndexing_SkippedWhenDisposedBeforeCompletion()
    {
        var order = new List<string>();
        var tcs = new TaskCompletionSource();
        var doc = new TestDocument
        {
            DisposeOrder = order,
            AttachedSession = new RecordingSession(order) { IndexingTask = tcs.Task },
        };

        doc.MonitorIndexing();
        doc.Dispose();
        tcs.SetResult();
        await tcs.Task;
        await Task.Yield();
        await Task.Yield();

        Assert.False(doc.CompletedWasCalled);
        Assert.False(doc.FailedWasCalled);
    }

    /// <summary>
    /// The staleness guard RawViewModel relies on: a completion racing a restart that replaced
    /// the session's indexing task must not react for the retired scan. Asserted on both the
    /// success and the failure path, since a retired scan usually ends CANCELLED (the restart
    /// cancels it) and so arrives via the catch.
    /// </summary>
    [Fact]
    public async Task MonitorIndexing_SkippedWhenTheSessionHasMovedOnToANewTask()
    {
        var order = new List<string>();
        var retired = new TaskCompletionSource();
        var session = new RecordingSession(order) { IndexingTask = retired.Task };
        var doc = new TestDocument { AttachedSession = session };

        doc.MonitorIndexing(); // awaits retired.Task
        session.IndexingTask = new TaskCompletionSource().Task; // the "restart"
        retired.SetResult(); // the retired scan's task finally resolves

        await retired.Task;
        await Task.Yield();
        await Task.Yield();

        Assert.False(doc.CompletedWasCalled); // never reacted for the retired scan
        Assert.False(doc.FailedWasCalled);
    }

    /// <inheritdoc cref="MonitorIndexing_SkippedWhenTheSessionHasMovedOnToANewTask"/>
    [Fact]
    public async Task MonitorIndexing_SkippedWhenARetiredTaskIsCancelledAfterARestart()
    {
        var order = new List<string>();
        var retired = new TaskCompletionSource();
        var session = new RecordingSession(order) { IndexingTask = retired.Task };
        var doc = new TestDocument { AttachedSession = session };

        doc.MonitorIndexing();
        session.IndexingTask = new TaskCompletionSource().Task;
        retired.SetCanceled();

        await Task.Yield();
        await Task.Yield();
        await Task.Delay(20);

        Assert.False(doc.FailedWasCalled);
        Assert.False(doc.CompletedWasCalled);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
            await Task.Delay(5);

        Assert.True(condition());
    }
}
