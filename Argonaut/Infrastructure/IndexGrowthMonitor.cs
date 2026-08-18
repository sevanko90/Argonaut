using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Argonaut.Infrastructure;

/// <summary>
/// Drives a UI collection's "the background scan grew, refresh what's visible" cadence,
/// extracted verbatim from JsonVisibleRowCollection so the diff row collection can run the
/// identical live-append behaviour against a diff task instead of a token index.
///
/// Two signals feed <paramref name="refresh"/> (which owns deciding whether anything
/// actually changed - e.g. the settled/last-count checks stay with the caller):
///
///  - A background-priority timer tick per <paramref name="interval"/>. The interval is a
///    deliberate trade against a known Avalonia virtualization glitch - see the caller's
///    GrowthPollInterval remarks.
///  - <paramref name="completionTask"/> completing (success, failure or cancellation
///    alike). Without this, a scan that finishes well within one poll interval would keep
///    showing its construction-time state until the next tick caught up - a multi-second
///    wait on work that has actually already finished. The await grants one immediate
///    final refresh the moment the scan stops, independent of the poll cadence.
///
/// UI-thread only: construct where the owning collection is constructed (the UI thread),
/// so the completion await resumes there too per the app's threading convention.
/// </summary>
public sealed class IndexGrowthMonitor : IDisposable
{
    private readonly Func<bool> isComplete;
    private readonly Action refresh;
    private DispatcherTimer? timer;

    public IndexGrowthMonitor(TimeSpan interval, Task completionTask, Func<bool> isComplete, Action refresh)
    {
        this.isComplete = isComplete;
        this.refresh = refresh;

        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += OnTick;
        timer.Start();

        _ = AwaitCompletionAsync(completionTask);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Read completion BEFORE refreshing, so the tick that observes completion still
        // refreshes one final time before the timer stops.
        bool complete = isComplete();

        refresh();

        if (complete)
            Stop();
    }

    private async Task AwaitCompletionAsync(Task completionTask)
    {
        try
        {
            await completionTask;
        }
        catch
        {
            // Failure/cancellation is the scan owner's to report; only "it stopped" matters
            // here (the final refresh must run either way).
        }

        if (timer is null)
            return; // disposed, or a regular tick already ran the final refresh and stopped this

        refresh();
        Stop();
    }

    private void Stop()
    {
        if (timer is null)
            return;

        timer.Stop();
        timer.Tick -= OnTick;
        timer = null;
    }

    public void Dispose() => Stop();
}
