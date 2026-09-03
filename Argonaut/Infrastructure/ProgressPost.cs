using System;
using Avalonia.Threading;

namespace Argonaut.Infrastructure;

/// <summary>
/// Hands a progress update to the UI thread from a scan running on a background one - the
/// <c>Dispatcher.UIThread.Post</c> half of the threading convention in CLAUDE.md - and
/// guarantees the scan cannot die of it.
///
/// That guarantee is the whole point. A scan calls <see cref="IProgressReporter.Report"/>
/// inline on its own thread, so anything the post throws unwinds out of the scan body, is
/// recorded as the index's <c>Failure</c> and stops the indexing - and a cosmetic status line
/// then presents itself to the user as a file that failed to index, or to a diff as a side
/// with no tokens and therefore no rows at all. Nothing about a percentage in a status bar is
/// worth that.
///
/// Real bug: <c>Dispatcher.Post</c> throws <see cref="NullReferenceException"/> out of
/// <c>RequestForegroundProcessing</c> when no dispatcher loop is running, which faulted the
/// right-hand index of a diff and left the document silently empty.
/// </summary>
public static class ProgressPost
{
    /// <summary>Runs <paramref name="apply"/> on the UI thread, or gives up quietly.</summary>
    public static void ToUiThread(Action apply)
    {
        try
        {
            Dispatcher.UIThread.Post(apply);
        }
        catch (Exception)
        {
            // Swallowed deliberately, and only around the handoff itself: apply runs later on
            // the UI thread, where its own failures are the dispatcher's to surface.
        }
    }
}
