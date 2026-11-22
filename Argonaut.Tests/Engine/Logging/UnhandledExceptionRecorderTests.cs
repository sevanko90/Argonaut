using Argonaut.Engine.Logging;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Logging;

/// <summary>
/// An exception nothing caught is written to the log with its stack, and one that ends the process
/// is already in the file when the handler returns - the runtime terminates straight after.
/// </summary>
public sealed class UnhandledExceptionRecorderTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("startup went wrong");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void ATerminatingException_IsInTheFile_WhenTheHandlerReturns()
    {
        string path = Path.Combine(dir, "debug.log");
        var log = new FileDiagnosticLog(path);

        new UnhandledExceptionRecorder(log).OnUnhandledException(null, new UnhandledExceptionEventArgs(Thrown(), isTerminating: true));

        string written = File.ReadAllText(path);
        Assert.Contains("Unhandled exception, terminating: System.InvalidOperationException: startup went wrong", written);
        Assert.Contains(nameof(Thrown), written);
    }

    [Fact]
    public void AnUnobservedTaskException_IsRecorded()
    {
        var log = new RecordingDiagnosticLog();

        new UnhandledExceptionRecorder(log).OnUnobservedTaskException(null, new UnobservedTaskExceptionEventArgs(new AggregateException(Thrown())));

        Assert.StartsWith("Unobserved task exception: System.AggregateException", Assert.Single(log.Lines));
    }
}
