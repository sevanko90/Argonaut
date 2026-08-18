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

    private int? selectedPosition;
    private bool sourceShowsPath;
    private bool targetShowsPath;
    private string sourcePrefix = string.Empty;
    private string sourceChanged = string.Empty;
    private string sourceSuffix = string.Empty;
    private string targetPrefix = string.Empty;
    private string targetChanged = string.Empty;
    private string targetSuffix = string.Empty;

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

    // ── Selection and the source/target context bar ────────────────────────────────────

    /// <summary>
    /// Visible-list position of the selected row, or null. Set by the view on click and by
    /// the go-to-next/previous-diff actions (the view mirrors changes back into the
    /// ListBox); every change recomputes the context bar below.
    /// </summary>
    public int? SelectedPosition
    {
        get => selectedPosition;
        set
        {
            if (!SetField(ref selectedPosition, value))
                return;

            UpdateContext();
        }
    }

    public bool HasSelection => SelectedRow is not null;

    private JsonDiffRow? SelectedRow =>
        rows is { } r && selectedPosition is { } p && p >= 0 && p < r.Count
            ? r[p] as JsonDiffRow
            : null;

    public void GoToNextDiff() => GoToDiff(1);

    public void GoToPreviousDiff() => GoToDiff(-1);

    private void GoToDiff(int direction)
    {
        if (rows is not { } r)
            return;

        if (r.FindNextChange(selectedPosition ?? -1, direction) is { } position)
            SelectedPosition = position;
    }

    /// <summary>Per-row display mode of the context bar: the selected value (default) or
    /// the row's JSONPath. Independent per side, toggled by the bar's swap buttons.</summary>
    public bool SourceShowsPath
    {
        get => sourceShowsPath;
        set
        {
            if (SetField(ref sourceShowsPath, value))
            {
                OnPropertyChanged(nameof(SourceModeLabel));
                UpdateContext();
            }
        }
    }

    public bool TargetShowsPath
    {
        get => targetShowsPath;
        set
        {
            if (SetField(ref targetShowsPath, value))
            {
                OnPropertyChanged(nameof(TargetModeLabel));
                UpdateContext();
            }
        }
    }

    /// <summary>The swap buttons name what clicking switches TO.</summary>
    public string SourceModeLabel => sourceShowsPath ? "value" : "path";

    public string TargetModeLabel => targetShowsPath ? "value" : "path";

    public void ToggleSourceMode() => SourceShowsPath = !SourceShowsPath;

    public void ToggleTargetMode() => TargetShowsPath = !TargetShowsPath;

    // The context lines are split into prefix/changed/suffix runs so the view can paint
    // just the differing characters. Path mode and no-diff cases put everything in the
    // prefix run.
    public string SourcePrefix { get => sourcePrefix; private set => SetField(ref sourcePrefix, value); }
    public string SourceChanged { get => sourceChanged; private set => SetField(ref sourceChanged, value); }
    public string SourceSuffix { get => sourceSuffix; private set => SetField(ref sourceSuffix, value); }
    public string TargetPrefix { get => targetPrefix; private set => SetField(ref targetPrefix, value); }
    public string TargetChanged { get => targetChanged; private set => SetField(ref targetChanged, value); }
    public string TargetSuffix { get => targetSuffix; private set => SetField(ref targetSuffix, value); }

    private void UpdateContext()
    {
        var row = SelectedRow;
        OnPropertyChanged(nameof(HasSelection));

        if (row is null || row.IsPlaceholder || session is null)
        {
            (SourcePrefix, SourceChanged, SourceSuffix) = (string.Empty, string.Empty, string.Empty);
            (TargetPrefix, TargetChanged, TargetSuffix) = (string.Empty, string.Empty, string.Empty);
            return;
        }

        // Deliberately the rows' own display strings: JsonRowFactory built them through
        // DisplayText.Read, so they are already capped at DisplayText.MaxLength (1KB) with
        // an ellipsis - a pathological multi-MB scalar never gets decoded here, and the
        // char-diff below runs over at most 1KB per side.
        string? leftValue = row.Left?.Value;
        string? rightValue = row.Right?.Value;

        // Only an Added/Removed row's lone value is "all change"; a Moved row's content is
        // unchanged by definition, so its single side renders plain.
        bool highlightWhole = row.Status is DiffStatus.Added or DiffStatus.Removed;

        (SourcePrefix, SourceChanged, SourceSuffix) = sourceShowsPath
            ? (PathFor(row, target: false) ?? string.Empty, string.Empty, string.Empty)
            : ContextRuns(leftValue, rightValue, highlightWhole);

        (TargetPrefix, TargetChanged, TargetSuffix) = targetShowsPath
            ? (PathFor(row, target: true) ?? string.Empty, string.Empty, string.Empty)
            : ContextRuns(rightValue, leftValue, highlightWhole);
    }

    private string? PathFor(JsonDiffRow row, bool target)
    {
        if (session is not { } s)
            return null;

        // Mirrored rows (unchanged subtrees) reuse the left JsonRow on both panes; the
        // left-derived path is structurally valid for the right document there, since the
        // subtree is identical by definition.
        if (target && row.Right is { } right && !ReferenceEquals(row.Right, row.Left))
            return JsonPathBuilder.Build(s.Right.Index, s.Right.File, right.TokenIndex);

        return row.Left is { } left ? JsonPathBuilder.Build(s.Left.Index, s.Left.File, left.TokenIndex)
            : row.Right is { } r ? JsonPathBuilder.Build(s.Right.Index, s.Right.File, r.TokenIndex)
            : null;
    }

    private static (string Prefix, string Changed, string Suffix) ContextRuns(string? value, string? other, bool highlightWhole)
    {
        if (value is null)
            return (string.Empty, string.Empty, string.Empty);

        if (other is null)
            return highlightWhole ? (string.Empty, value, string.Empty) : (value, string.Empty, string.Empty);

        return SplitByCommonAffixes(value, other);
    }

    /// <summary>
    /// The character-level diff behind the context bar's highlight: the longest common
    /// prefix and suffix bracket the span that actually differs. Inputs are the
    /// display-capped row values (never wrapped, so a single differing span reads well);
    /// identical strings yield an empty Changed run.
    /// </summary>
    internal static (string Prefix, string Changed, string Suffix) SplitByCommonAffixes(string value, string other)
    {
        int prefix = 0;
        int max = Math.Min(value.Length, other.Length);
        while (prefix < max && value[prefix] == other[prefix])
            prefix++;

        int suffix = 0;
        while (suffix < max - prefix && value[value.Length - 1 - suffix] == other[other.Length - 1 - suffix])
            suffix++;

        return (value[..prefix], value[prefix..(value.Length - suffix)], value[^suffix..]);
    }

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
            setChangesOnly: value => { if (rows is { } r) r.ChangesOnly = value; },
            goToPreviousDiff: GoToPreviousDiff,
            goToNextDiff: GoToNextDiff);

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
