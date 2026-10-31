using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;

namespace Argonaut.Tests.Support;

/// <summary>
/// An <see cref="ILineEndingDetector"/> that records each origin it is asked about. By default each
/// detection completes at once with <see cref="Answer"/>; with <see cref="HoldsResults"/> each one
/// waits until the test calls <see cref="Complete"/>, so the order of a detection against a save or
/// an input switch is set by the test rather than by timing.
/// </summary>
internal sealed class FakeLineEndingDetector : ILineEndingDetector
{
    private readonly List<(IByteOrigin Origin, TaskCompletionSource<LineEndingTally> Result)> pending = new();

    public LineEndingTally Answer { get; set; } = new(Lf: 3, CrLf: 0, LoneCr: 0, SampledBytes: 12, WholeInput: true);

    public bool HoldsResults { get; init; }

    public List<IByteOrigin> Asked { get; } = new();

    /// <summary>Detections asked for and not yet completed or cancelled.</summary>
    public int Pending => this.pending.Count(p => !p.Result.Task.IsCompleted);

    public Task<LineEndingTally> DetectAsync(IByteOrigin origin, CancellationToken stopping)
    {
        Asked.Add(origin);
        if (!HoldsResults)
            return Task.FromResult(Answer);

        var result = new TaskCompletionSource<LineEndingTally>(TaskCreationOptions.RunContinuationsAsynchronously);
        stopping.Register(() => result.TrySetCanceled(stopping));
        this.pending.Add((origin, result));
        return result.Task;
    }

    /// <summary>Completes the oldest detection still waiting with <paramref name="tally"/>.</summary>
    public void Complete(LineEndingTally tally)
        => this.pending.First(p => !p.Result.Task.IsCompleted).Result.SetResult(tally);
}
