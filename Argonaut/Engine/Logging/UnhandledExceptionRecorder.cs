using System;
using System.Threading.Tasks;

namespace Argonaut.Engine.Logging;

/// <summary>
/// Writes an exception nothing caught into the diagnostic log, with its stack, so a crash leaves
/// a reason behind - including one at startup, before the window exists, which otherwise leaves
/// only an OS crash report with no managed frames in it.
///
/// Attached first thing in <c>Program.Main</c>. When the exception ends the process, the log is
/// flushed before the handler returns: the runtime terminates as soon as it does, and a queued
/// line would be lost. A faulted task nobody awaited is recorded too; it does not end the process.
/// </summary>
public sealed class UnhandledExceptionRecorder(IDiagnosticLog log)
{
    /// <summary>Starts recording what <paramref name="domain"/> and the task scheduler report.</summary>
    public void Attach(AppDomain domain)
    {
        domain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        log.Write($"Unhandled exception{(e.IsTerminating ? ", terminating" : string.Empty)}: {e.ExceptionObject}");
        if (e.IsTerminating && log is IDisposable flushing)
            flushing.Dispose();
    }

    internal void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => log.Write($"Unobserved task exception: {e.Exception}");
}
