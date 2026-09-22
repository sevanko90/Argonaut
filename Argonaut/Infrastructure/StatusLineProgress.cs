using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// Writes a background scan's progress onto a status line: "Indexing orders.json… (45%)".
/// <see cref="Report"/> is called from the scan thread, so it marshals with
/// <see cref="ProgressPost.ToUiThread"/> (never a blocking InvokeAsync) per the app's threading
/// convention, and posts at most once per 5% so a byte-offset stream does not flood the UI thread.
///
/// Progress is a temporary claim on the line: while a scan runs, the document's own text is a
/// stale partial count ("250 rows indexed so far"), so live progress is the more useful thing to
/// show. Once the scan stops the owner's final text must win - see <see cref="Stop"/>.
///
/// Used by the shell for every load, and by a document for the re-indexes it starts itself (the
/// raw view's wrap-width change, and its reopen after a save), which happen after the shell's
/// reporter for that document has stopped.
/// </summary>
public sealed class StatusLineProgress : IProgressReporter
{
    private const int BucketSize = 5;

    private readonly string subject;
    private readonly Func<bool> stillWanted;
    private readonly Action<string> show;
    private int lastBucket = -1;

    // Set on the UI thread once the scan stops; read on the UI thread inside the posted update.
    // Volatile because Report itself runs on the scan thread.
    private volatile bool stopped;

    /// <param name="subject">What is being scanned, as the line should name it - usually a path.</param>
    /// <param name="stillWanted">False once whatever this reports for has been superseded (a newer
    /// open, a disposed document). Checked on both threads, so it must be safe to call from either.</param>
    /// <param name="show">Puts the text on the line. Called on the UI thread only.</param>
    public StatusLineProgress(string subject, Func<bool> stillWanted, Action<string> show)
    {
        this.subject = subject;
        this.stillWanted = stillWanted;
        this.show = show;
    }

    /// <summary>
    /// Permanently stops this reporter writing to the line. Called on the UI thread when the scan
    /// completes, which is what keeps the final "N rows" from being overwritten by a trailing
    /// "Indexing… (100%)": the last reports are posted from the scan thread just before it
    /// finishes, so they can still be sitting in the dispatcher queue. Re-checking the flag inside
    /// the posted action (rather than only before posting) is what drops those - both sides of
    /// that check run on the UI thread, so there is no race left.
    /// </summary>
    public void Stop() => stopped = true;

    public void Report(string message, long? current = null, long? max = null)
    {
        if (stopped || !stillWanted())
            return;

        string text = $"{message} {subject}…";

        if (current.HasValue && max.HasValue && max.Value > 0)
        {
            int percent = (int)Math.Min(100, (current.Value * 100L) / max.Value);

            int bucket = percent / BucketSize;
            if (bucket == lastBucket)
                return;

            lastBucket = bucket;
            text += $" ({percent}%)";
        }

        ProgressPost.ToUiThread(() =>
        {
            if (!stopped && stillWanted())
                show(text);
        });
    }
}
