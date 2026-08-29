namespace Argonaut.Infrastructure;

/// <summary>
/// The codebase's staleness idiom (see <see cref="Argonaut.Features.Search.FindController"/>'s
/// class remarks), formalized as one primitive instead of three hand-rolled monotonic counters
/// (<c>MainWindowViewModel.openRequestId</c>, <c>NdJsonViewModel.selectionRequestId</c>,
/// <c>FindController.requestId</c>): a request begins, gets a ticket, and any continuation that
/// resumes later checks whether its ticket is still the active one before acting on stale
/// results.
///
/// Sealed class, not a struct: <see cref="Begin"/> mutates, so a struct would have to live in a
/// plain mutable field and never be exposed through a property or a <c>readonly</c> field - a
/// copy would silently get its own counter and every staleness check would pass forever. Some
/// callers read this from a continuation that can resume off the UI thread (a fire-and-forget
/// completion handler whose <c>await</c> resumes wherever the awaited task happened to
/// complete), which makes an accidental copy even harder to spot than a same-thread one.
///
/// Deliberately NOT volatile/interlocked, despite those cross-thread reads (and note C# cannot
/// mark a <c>long</c> volatile at all - it would have to be <c>Volatile.Read</c>). Every
/// off-thread read is a fast-path early-out that a UI-thread check then repeats before anything
/// is acted on: <see cref="Argonaut.Shell.MainWindowViewModel"/>'s StatusProgressReporter.Report
/// runs on the scan thread and tests the ticket only to skip formatting a status line, then
/// tests it again inside its <c>Dispatcher.Post</c> before writing one. A stale read there costs
/// one discarded string, never a wrong write. A reader who needs the check to be authoritative
/// must make it from the UI thread - which is where every consumer already makes it.
/// </summary>
public sealed class RequestTicket
{
    private long current;

    /// <summary>The ticket current right now, without issuing a new one - for a reader that
    /// captures "whatever is active at this moment" alongside a request it doesn't itself begin
    /// (e.g. a progress reporter created for the request that is about to start).</summary>
    public long Current => current;

    /// <summary>Starts a new request and returns its ticket.</summary>
    public long Begin() => ++current;

    /// <summary>True if <paramref name="ticket"/> is still the active one.</summary>
    public bool IsCurrent(long ticket) => ticket == current;
}
