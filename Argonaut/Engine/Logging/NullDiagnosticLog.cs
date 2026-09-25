namespace Argonaut.Engine.Logging;

/// <summary>Keeps nothing: the log a shipped build and the tests are given.</summary>
public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static NullDiagnosticLog Instance { get; } = new();

    private NullDiagnosticLog()
    {
    }

    public void Write(string message)
    {
    }
}
