namespace JsonViewerCore.Infrastructure;

/// <summary>
/// Sink for progress updates. Producers just hand over the raw current/max values (or
/// null when a total isn't known/meaningful) - the implementation owns computing the
/// percentage, formatting it into the message, and deciding how often that's worth
/// acting on (e.g. only once per 5% step).
/// </summary>
public interface IProgressReporter
{
    void Report(string message, long? current = null, long? max = null);
}
