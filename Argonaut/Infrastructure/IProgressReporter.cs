namespace Argonaut.Infrastructure;

/// <summary>
/// Sink for progress updates. A producer reports how far it has got - <c>current</c> of
/// <c>max</c> - whenever it is cheap for it to do so; that is all a reporter needs, and the
/// reporter alone decides what to make of it: the percentage, and how finely to show it (see
/// <see cref="ProgressEntry"/>). A producer never computes a percentage or sizes its reporting to
/// one - that would fix the display's granularity inside a scan loop.
///
/// <paramref name="message"/> in <see cref="Report"/> names the stage of the work ("Indexing",
/// "Comparing"); the reporter may show it or not.
/// </summary>
public interface IProgressReporter
{
    /// <summary>Reports <paramref name="current"/> of <paramref name="max"/> done, or just the
    /// stage when there is no total.</summary>
    void Report(string message, long? current = null, long? max = null);
}
