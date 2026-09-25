using System;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Argonaut.Engine.Logging;

/// <summary>
/// A diagnostic log appended to one file. <see cref="Write"/> only queues the line; a background
/// task does the file work, so a caller on the UI thread never waits on the disk.
///
/// Kept small by truncation rather than rotation: a file already over <see cref="MaxBytes"/> when
/// the log starts is emptied, and that is all. A session can take it past the limit, and the next
/// start clears it - there are no numbered siblings to find and delete. Appended in append mode,
/// so two instances of the app writing at once interleave whole lines rather than overwriting
/// each other.
/// </summary>
public sealed class FileDiagnosticLog : IDiagnosticLog, IDisposable
{
    /// <summary>The size past which the file is emptied when the log starts.</summary>
    public const long MaxBytes = 1024 * 1024;

    private readonly string path;
    private readonly long maxBytes;
    private readonly Channel<string> pending = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writing;

    /// <param name="path">The log file; created, with its folder, if missing.</param>
    /// <param name="maxBytes">See <see cref="MaxBytes"/>; smaller only in tests.</param>
    public FileDiagnosticLog(string path, long maxBytes = MaxBytes)
    {
        this.path = path;
        this.maxBytes = maxBytes;
        this.writing = Task.Run(WriteQueuedLinesAsync);
    }

    /// <summary>Stamped now, on the caller's thread, so a line's time is when it happened rather
    /// than when the writer got to it.</summary>
    public void Write(string message) =>
        this.pending.Writer.TryWrite($"{DateTime.Now:HH:mm:ss.fff} {message}");

    /// <summary>
    /// Stops taking lines and waits briefly for the queued ones to reach the file - at app exit,
    /// where the last lines are often the interesting ones. The wait is bounded so a stuck disk
    /// cannot hold up the exit.
    /// </summary>
    public void Dispose()
    {
        this.pending.Writer.TryComplete();
        try { this.writing.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* the writer swallows its own failures; nothing to report here */ }
    }

    private async Task WriteQueuedLinesAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            TruncateIfOversized();

            using var file = new FileStream(this.path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            while (await this.pending.Reader.WaitToReadAsync())
            {
                while (this.pending.Reader.TryRead(out var line))
                    writer.WriteLine(line);

                // Once per burst, so the file is current whenever the queue runs dry - a log
                // someone is tailing, or one read after a crash, shows what has happened so far.
                writer.Flush();
            }
        }
        catch
        {
            // Diagnostics must never break the app. A log that cannot be written is simply not
            // written: closing the queue makes every later Write a no-op, and draining it drops
            // what was already waiting, so nothing accumulates for the rest of the session.
            this.pending.Writer.TryComplete();
            while (this.pending.Reader.TryRead(out _))
            {
            }
        }
    }

    private void TruncateIfOversized()
    {
        var info = new FileInfo(this.path);
        if (!info.Exists || info.Length <= this.maxBytes)
            return;

        using var file = new FileStream(this.path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }
}
