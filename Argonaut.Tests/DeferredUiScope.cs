using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Captures the work <see cref="UiDeferral"/> would post to the dispatcher and runs it on demand,
/// so view-model tests stay dispatcher-free (these classes never start a headless session) while
/// still exercising the real deferral: work only happens when the test says <see cref="Pump"/>,
/// which is exactly what the running app's next dispatcher turn does.
/// </summary>
internal sealed class DeferredUiScope : IDisposable
{
    private readonly List<Action> pending = new();

    public DeferredUiScope() => UiDeferral.PostOverride = pending.Add;

    /// <summary>Runs everything queued, including anything those actions queue in turn.</summary>
    public void Pump()
    {
        while (pending.Count > 0)
        {
            var batch = pending.ToArray();
            pending.Clear();
            foreach (var action in batch)
                action();
        }
    }

    public void Dispose() => UiDeferral.PostOverride = null;
}
