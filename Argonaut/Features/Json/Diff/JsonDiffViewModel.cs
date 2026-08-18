using System;
using System.IO;
using System.Threading.Tasks;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;
using Avalonia.Threading;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// The diff document (diff plan stage 5): owns a <see cref="JsonDiffSession"/> internally,
/// which keeps MainWindowViewModel's single-CurrentDocument invariant intact - the shell
/// treats a diff exactly like any other document. Entered explicitly ("Compare with…"),
/// never via FileTypeDetector, so it claims no FileKind and the view switcher doesn't
/// offer it; switching away disposes it via the normal outgoing-document path.
///
/// Find is unavailable in v1 (<see cref="CreateSearchNavigator"/> returns null and the
/// find bar hides, exactly like IncompatibleViewModel). A side's indexing failure is
/// surfaced through <see cref="IndexFailure"/> with the side named in the message - the
/// shell's existing zero-progress/partial-progress handling then applies unchanged.
/// </summary>
public sealed class JsonDiffViewModel : ObservableObject, IDocumentViewModel
{
    private JsonDiffSession? session;
    private JsonDiffRowCollection? rows;
    private string statusText = string.Empty;
    private IndexFailure? indexFailure;
    private volatile bool disposed;

    public string FilePath { get; private set; } = string.Empty;

    public string RightFilePath { get; private set; } = string.Empty;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public IndexFailure? IndexFailure
    {
        get => indexFailure;
        private set => SetField(ref indexFailure, value);
    }

    public JsonDiffRowCollection Rows => rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    public JsonDiffToolbarViewModel? Toolbar { get; private set; }

    object? IDocumentViewModel.Toolbar => Toolbar;

    public Task IndexingTask => session?.Diff.IndexingTask ?? Task.CompletedTask;

    public ISearchNavigator? CreateSearchNavigator() => null;

    public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

    /// <summary>
    /// Opens both files and starts the pipeline. Returns once the row collection exists
    /// (it renders the left-document preview immediately); indexing and the diff continue
    /// in the background, monitored for status/failure updates.
    /// </summary>
    public async Task LoadAsync(string leftPath, string rightPath)
    {
        FilePath = leftPath;
        RightFilePath = rightPath;

        var session = JsonDiffSession.Start(leftPath, rightPath,
            leftProgress: new ProgressToStatus(this, "Indexing " + Path.GetFileName(leftPath)),
            rightProgress: new ProgressToStatus(this, "Indexing " + Path.GetFileName(rightPath)),
            diffProgress: new ProgressToStatus(this, "Comparing"));
        this.session = session;

        Toolbar = new JsonDiffToolbarViewModel(
            Path.GetFileName(leftPath), Path.GetFileName(rightPath),
            setChangesOnly: value => { if (rows is { } r) r.ChangesOnly = value; });

        // A small initial batch so the preview's first paint isn't empty (mirrors
        // JsonViewModel.LoadCore); a tiny file completes the wait via MarkComplete instead.
        await session.Left.Index.WaitForTokenCountAsync(250);
        if (disposed)
            return;

        rows = new JsonDiffRowCollection(session);
        StatusText = $"Comparing {FilePath} with {RightFilePath}";

        _ = MonitorAsync(session);
    }

    /// <summary>
    /// Watches both sides and the diff to keep the status line and failure state current.
    /// Runs on the UI thread (fire-and-forget from LoadAsync; awaits resume there per the
    /// app's threading convention).
    /// </summary>
    private async Task MonitorAsync(JsonDiffSession session)
    {
        try
        {
            await session.Diff.IndexingTask;
        }
        catch
        {
            // Cancellation (document closed) or a worker fault; failure state below.
        }

        if (disposed)
            return;

        // Attribute a side failure - the diff completes empty in that case, and the shell's
        // existing IndexFailure handling (banner or incompatible placeholder) takes over.
        if (session.Left.Index.Failure is { } leftFailure)
        {
            IndexFailure = new IndexFailure($"Left file: {leftFailure.Message}", leftFailure.ByteOffset, leftFailure.Line, leftFailure.Column, leftFailure.ItemsIndexed);
            StatusText = $"{FilePath} — left file failed to index";
            return;
        }

        if (session.Right.Index.Failure is { } rightFailure)
        {
            IndexFailure = new IndexFailure($"Right file: {rightFailure.Message}", rightFailure.ByteOffset, rightFailure.Line, rightFailure.Column, rightFailure.ItemsIndexed);
            StatusText = $"{RightFilePath} — right file failed to index";
            return;
        }

        if (!session.Diff.IsComplete)
            return;

        StatusText = $"{Path.GetFileName(FilePath)} ↔ {Path.GetFileName(RightFilePath)} — {Summarize(session.Diff)}";
    }

    /// <summary>One pass over the finished record log, counting user-meaningful changes:
    /// whole-subtree adds/removes, modified leaves (a descended Modified container is
    /// structure, not itself a change), and move destinations.</summary>
    private static string Summarize(JsonDiffIndex diff)
    {
        int added = 0, removed = 0, modified = 0, moved = 0;
        for (int i = 0; i < diff.RecordCount; i++)
        {
            var record = diff.GetRecord(i);
            switch (record.Status)
            {
                case DiffStatus.Added: added++; break;
                case DiffStatus.Removed: removed++; break;
                case DiffStatus.Moved when !record.IsMoveSource: moved++; break;
                case DiffStatus.Modified when record.SubtreeEnd == record.Index + 1 || record.IsAlignmentApproximate:
                    modified++;
                    break;
            }
        }

        if (added + removed + modified + moved == 0)
            return "documents are identical";

        return $"{added:N0} added, {removed:N0} removed, {modified:N0} modified, {moved:N0} moved";
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // rows first (stops its growth monitor, which reads the record log and both
        // mappings), then the session (cancel -> join diff -> release both mappings).
        rows?.Dispose();
        session?.Dispose();
    }

    /// <summary>Marshals background progress reports onto the status line - same shape as
    /// the shell's StatusProgressReporter: Post (never blocking), ~5% buckets, silent once
    /// the view model is disposed.</summary>
    private sealed class ProgressToStatus : IProgressReporter
    {
        private readonly JsonDiffViewModel owner;
        private readonly string label;
        private int lastBucket = -1;

        public ProgressToStatus(JsonDiffViewModel owner, string label)
        {
            this.owner = owner;
            this.label = label;
        }

        public void Report(string message, long? current = null, long? max = null)
        {
            string text = label;
            if (current.HasValue && max.HasValue && max.Value > 0)
            {
                int percent = (int)Math.Min(100, current.Value * 100L / max.Value);
                int bucket = percent / 5;
                if (bucket == lastBucket)
                    return;
                lastBucket = bucket;
                text += $"… ({percent}%)";
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!owner.disposed)
                    owner.StatusText = text;
            });
        }
    }
}
