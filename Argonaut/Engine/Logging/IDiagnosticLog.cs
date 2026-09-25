namespace Argonaut.Engine.Logging;

/// <summary>
/// Where diagnostic lines go: what happened on the way through an open, a load or a failure,
/// for whoever is debugging a local build. Not a user-facing record - a shipped build is given
/// <see cref="NullDiagnosticLog"/> and keeps nothing.
///
/// Composed at the root and handed down, like the settings store: nothing below the root decides
/// where a log lives or opens a file to write one.
/// </summary>
public interface IDiagnosticLog
{
    /// <summary>Records one line. Returns at once and never throws - logging must not be able to
    /// break, or slow, what it is describing. Safe from any thread.</summary>
    void Write(string message);
}
