using System.Collections.Concurrent;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Tests.Support;

/// <summary>
/// Stands in for the UI dispatcher in view-model tests, which never start a headless session:
/// captures the work <see cref="UiDeferral"/> would post, and runs it on demand. Work only
/// happens when the test says <see cref="Pump"/>, which is exactly what the running app's next
/// dispatcher turn does.
///
/// It also stands in for the dispatcher's <see cref="SynchronizationContext"/>. Anything a pumped
/// action awaits resumes back here - queued, and run on the test thread by a later
/// <see cref="Pump"/> - rather than on whichever pool thread finished the work. Without that, a
/// continuation the app would run on the UI thread runs concurrently with the test's own
/// assertions, and a test can observe a half-applied update the app never could (a schema load
/// had set <c>Document</c> but not yet raised the notifications that act on it).
/// </summary>
internal sealed class DeferredUiScope : IDisposable
{
    // Concurrent because continuations are posted from the pool thread that finished the work.
    private readonly ConcurrentQueue<Action> pending = new();
    private readonly PumpedContext context;

    public DeferredUiScope()
    {
        context = new PumpedContext(pending);
        UiDeferral.PostOverride = pending.Enqueue;
    }

    /// <summary>Runs everything queued, including anything those actions queue in turn, as the UI
    /// thread would: under this scope's context, so their awaits come back to the queue.</summary>
    public void Pump()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            while (pending.TryDequeue(out var action))
                action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Pumps until <paramref name="settled"/> holds, for work that finishes off-thread and
    /// then posts back - the pump is where its continuation runs. Gives up after about a second,
    /// leaving the caller's assertion to report what did not happen.</summary>
    public async Task PumpUntilAsync(Func<bool> settled)
    {
        for (int i = 0; i < 100; i++)
        {
            Pump();
            if (settled())
                return;
            await Task.Delay(10);
        }
    }

    public void Dispose() => UiDeferral.PostOverride = null;

    private sealed class PumpedContext(ConcurrentQueue<Action> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}
