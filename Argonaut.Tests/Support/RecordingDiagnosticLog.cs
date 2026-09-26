using Argonaut.Engine.Logging;

namespace Argonaut.Tests.Support;

/// <summary>
/// A diagnostic log that keeps its lines in memory, for a test that needs to check what was
/// logged. No timestamps and no file, so an assertion is on the message alone and needs no wait
/// for a background writer.
/// </summary>
internal sealed class RecordingDiagnosticLog : IDiagnosticLog
{
    private readonly List<string> lines = new();

    /// <summary>Every line so far, in the order written - a copy, so a test can enumerate it while
    /// another thread is still logging.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (this.lines)
                return this.lines.ToArray();
        }
    }

    public void Write(string message)
    {
        lock (this.lines)
            this.lines.Add(message);
    }
}
