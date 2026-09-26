using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Search;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;
using Argonaut.Ui.Progress;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// The diff document: owns a <see cref="JsonDiffSession"/> internally, which keeps
/// MainWindowViewModel's single-CurrentDocument invariant intact - the shell treats a diff
/// exactly like any other document. Entered explicitly ("Compare with…"), never via
/// FileTypeDetector, so it claims no FileKind and the view switcher doesn't offer it; switching
/// away disposes it via the normal outgoing-document path.
///
/// Its rows are the merged diff tree (<see cref="JsonDiffTree"/>), drawn by the view's tree
/// surface - and before the comparison has anything to show, the left document alone, as a plain
/// JSON tree. The selection and the context bar below it, next/previous change and find all
/// speak in the merged tree's row keys.
///
/// Find runs over BOTH documents from the shell's one find bar - see
/// <see cref="JsonDiffSearchNavigator"/> for how the two scans interleave into a single
/// next/previous sequence. A side's indexing failure is surfaced through
/// <see cref="IndexFailure"/> with the side named in the message - the shell's existing
/// zero-progress/partial-progress handling then applies unchanged.
/// </summary>
public sealed class JsonDiffViewModel : IndexedDocumentViewModel
{
    /// <summary>How often the rows are told the comparison has more to show.</summary>
    private static readonly TimeSpan GrowthInterval = TimeSpan.FromMilliseconds(500);

    private JsonDiffSession? session;
    private JsonDiffTree? diffTree;
    private TreeDocument? preview;
    private ITreeRowSource? tree;
    private IndexGrowthMonitor? growthMonitor;

    private TreeRow? selectedRow;
    private bool sourceShowsPath;
    private bool targetShowsPath;
    private string sourcePrefix = string.Empty;
    private string sourceChanged = string.Empty;
    private string sourceSuffix = string.Empty;
    private string targetPrefix = string.Empty;
    private string targetChanged = string.Empty;
    private string targetSuffix = string.Empty;
    private string? sourcePlaceholder;
    private string? targetPlaceholder;
    private string? highlightTerm;

    protected override IDocumentSession? Session => session;

    protected override IDisposable? MappedRows => session is null ? null : new CloseTrees(this);

    public string RightFilePath { get; private set; } = string.Empty;

    /// <summary>The merged diff tree. Null until LoadAsync has started.</summary>
    public JsonDiffTree? DiffTree => diffTree;

    /// <summary>What the view's surface draws: the left document while the comparison has
    /// nothing to show yet, then the merged tree.</summary>
    public ITreeRowSource? Tree
    {
        get => tree;
        private set => SetField(ref tree, value);
    }

    private JsonDiffToolbarViewModel? toolbar;

    public override JsonDiffToolbarViewModel? Toolbar => toolbar;

    /// <summary>
    /// The active find term, highlighted in both panes. Null when no find is active.
    /// </summary>
    public string? HighlightTerm
    {
        get => highlightTerm;
        set => SetField(ref highlightTerm, value);
    }

    /// <summary>Completes once the comparison has stopped and its final refresh has run - what a
    /// test awaits before reading the rows (see <c>IndexGrowthMonitor.FinalRefreshTask</c>).</summary>
    internal Task FinalRefreshTask => growthMonitor?.FinalRefreshTask ?? Task.CompletedTask;

    /// <summary>One navigator over both documents - see <see cref="JsonDiffSearchNavigator"/>.
    /// Null before <see cref="LoadAsync"/> has produced a session, which is also the state an
    /// unusable diff is left in.</summary>
    public override ISearchNavigator? CreateSearchNavigator()
        => session is { } s && diffTree is not null ? new JsonDiffSearchNavigator(this, s) : null;

    // ── Reveals ────────────────────────────────────────────────────────────────────────

    /// <summary>The row key a reveal is waiting to show - consumed by the view, which scrolls to
    /// it and selects it.</summary>
    public long? PendingReveal { get; private set; }

    /// <summary>A reveal was asked for; the view shows <see cref="PendingReveal"/>.</summary>
    public event EventHandler? RevealRequested;

    /// <summary>The view has shown <see cref="PendingReveal"/>.</summary>
    public void ClearPendingReveal() => PendingReveal = null;

    /// <summary>Opens whatever hides the row with <paramref name="key"/>, selects it and asks the
    /// view to bring it into sight.</summary>
    private void Reveal(long key)
    {
        if (diffTree is null || !ReferenceEquals(Tree, diffTree))
            return;

        if (diffTree.Reveal(key) is not { } row)
            return;

        OnRowSelected(row);
        PendingReveal = row.Start;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reveals a find match: selects the merged row drawing the match's offset in the document it
    /// was found in, opening whatever stands in the way. Because a diff row carries both sides,
    /// landing on it brings the other document's counterpart into view too.
    /// </summary>
    public Task RevealMatchAsync(bool leftSide, SearchMatch match, CancellationToken ct)
    {
        if (diffTree is null || ct.IsCancellationRequested)
            return Task.CompletedTask;

        if (diffTree.KeyForMatch(leftSide, match.Offset) is { } key and not long.MaxValue)
            Reveal(key);
        return Task.CompletedTask;
    }

    /// <summary>Where a match sits in the merged order find steps through - see
    /// <see cref="JsonDiffTree.KeyForMatch"/>.</summary>
    public long? MatchOrderKey(bool leftSide, SearchMatch match)
        => diffTree is null ? match.Offset : diffTree.KeyForMatch(leftSide, match.Offset);

    // ── Selection and the source/target context bar ────────────────────────────────────

    /// <summary>The selected row, or null. The view reports it; next/previous change and find
    /// set it before asking the view to show it.</summary>
    public TreeRow? SelectedRow
    {
        get => selectedRow;
        private set => SetField(ref selectedRow, value);
    }

    /// <summary>The view's selection moved to <paramref name="row"/>: recomputes the context
    /// bar.</summary>
    public void OnRowSelected(TreeRow? row)
    {
        SelectedRow = row?.Detail is JsonDiffRowDetail ? row : null;
        UpdateContext();
    }

    public bool HasSelection => SelectedDetail is not null;

    private JsonDiffRowDetail? SelectedDetail => selectedRow?.Detail as JsonDiffRowDetail;

    public void GoToNextDiff() => GoToDiff(1);

    public void GoToPreviousDiff() => GoToDiff(-1);

    private void GoToDiff(int direction)
    {
        if (diffTree?.NextChange(selectedRow?.Start, direction) is { } key)
            Reveal(key);
    }

    /// <summary>"Changes only": the runs of unchanged pairs drop out of the rows.</summary>
    private void SetChangesOnly(bool value)
    {
        if (diffTree is null || diffTree.ChangesOnly == value)
            return;

        diffTree.ChangesOnly = value;
        ChangesOnlyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The rows shown changed without the document changing: the view reseats.</summary>
    public event EventHandler? ChangesOnlyChanged;

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

    /// <summary>Non-null when the row simply doesn't exist on the source side (Added, or the
    /// destination of a move) - the context bar shows this instead of an empty value line.</summary>
    public string? SourcePlaceholder { get => sourcePlaceholder; private set => SetField(ref sourcePlaceholder, value); }

    /// <summary>Same as <see cref="SourcePlaceholder"/>, for the target side.</summary>
    public string? TargetPlaceholder { get => targetPlaceholder; private set => SetField(ref targetPlaceholder, value); }

    /// <summary>Whether the source line's value/path runs (as opposed to
    /// <see cref="SourcePlaceholder"/>) should be shown.</summary>
    public bool ShowSourceValue => HasSelection && SourcePlaceholder is null;

    /// <summary>Whether the target line's value/path runs (as opposed to
    /// <see cref="TargetPlaceholder"/>) should be shown.</summary>
    public bool ShowTargetValue => HasSelection && TargetPlaceholder is null;

    private void UpdateContext()
    {
        var detail = SelectedDetail;
        OnPropertyChanged(nameof(HasSelection));

        if (detail is null || session is null || diffTree is null)
        {
            (SourcePrefix, SourceChanged, SourceSuffix) = (string.Empty, string.Empty, string.Empty);
            (TargetPrefix, TargetChanged, TargetSuffix) = (string.Empty, string.Empty, string.Empty);
            SourcePlaceholder = null;
            TargetPlaceholder = null;
            OnPropertyChanged(nameof(ShowSourceValue));
            OnPropertyChanged(nameof(ShowTargetValue));
            return;
        }

        // Each value is decoded through DisplayText, so capped at DisplayText.MaxLength (1KB)
        // with an ellipsis - a pathological multi-MB scalar never gets decoded here, and the
        // char-diff below runs over at most 1KB per side.
        string? leftValue = detail.Left is { } l ? diffTree.Left.Text.ValueText(l.Node) : null;
        string? rightValue = detail.Right is { } r ? (detail.RightMirrorsLeft ? diffTree.Left : diffTree.Right).Text.ValueText(r.Node) : null;

        // Absence is a property of the SIDE, not of path-vs-value mode: a side with no node has no
        // path to show any more than it has a value to show.
        SourcePlaceholder = leftValue is null ? AbsenceReason(detail) : null;
        TargetPlaceholder = rightValue is null ? AbsenceReason(detail) : null;

        (SourcePrefix, SourceChanged, SourceSuffix) = SourcePlaceholder is not null
            ? (string.Empty, string.Empty, string.Empty)
            : sourceShowsPath
                ? (PathFor(detail, target: false) ?? string.Empty, string.Empty, string.Empty)
                : TargetPlaceholder is not null
                    ? (leftValue ?? string.Empty, string.Empty, string.Empty)
                    : SplitByCommonAffixes(leftValue ?? string.Empty, rightValue ?? string.Empty);

        (TargetPrefix, TargetChanged, TargetSuffix) = TargetPlaceholder is not null
            ? (string.Empty, string.Empty, string.Empty)
            : targetShowsPath
                ? (PathFor(detail, target: true) ?? string.Empty, string.Empty, string.Empty)
                : SourcePlaceholder is not null
                    ? (rightValue ?? string.Empty, string.Empty, string.Empty)
                    : SplitByCommonAffixes(rightValue ?? string.Empty, leftValue ?? string.Empty);

        OnPropertyChanged(nameof(ShowSourceValue));
        OnPropertyChanged(nameof(ShowTargetValue));
    }

    /// <summary>Why a side of the selected row has nothing to show: a move says where its content
    /// is instead; Added/Removed fall back to a plain statement.</summary>
    private string AbsenceReason(JsonDiffRowDetail detail)
    {
        var record = diffTree!.Diff.GetRecord(detail.Record);
        if (detail.IsRecordRow && record.IsCrossParentMove)
        {
            return record.IsMoveSource
                ? $"moved to {diffTree.Right.Path(record.Right.ValueStart)} →"
                : $"↕ moved from {diffTree.Left.Path(record.Left.ValueStart)}";
        }

        if (detail.IsRecordRow && record.Status == DiffStatus.Moved)
            return $"↕ moved from [{record.LeftOrdinal}]";

        return record.Status switch
        {
            DiffStatus.Added => "property added — not present here",
            DiffStatus.Removed => "property deleted — not present here",
            _ => "not present here",
        };
    }

    private string? PathFor(JsonDiffRowDetail detail, bool target)
    {
        var diff = diffTree!;
        if (target && detail.Right is { } right)
        {
            if (!detail.RightMirrorsLeft)
                return diff.Right.Path(right.Node.ValueStart);

            // Drawn from the left document: splice the run element's real right-side path with
            // the structurally identical path below it, since the element's own index may differ
            // between the documents.
            if (RightElementPath(detail) is { } elementPath)
                return elementPath + diff.Left.RelativePath(right.Node.ValueStart, detail.MirrorLeftElement);
        }

        return detail.Left is { } left ? diff.Left.Path(left.Node.ValueStart)
            : detail.Right is { } other ? diff.Right.Path(other.Node.ValueStart)
            : null;
    }

    /// <summary>The right document's path to the run element holding a mirrored row.</summary>
    private string? RightElementPath(JsonDiffRowDetail detail)
    {
        var diff = diffTree!;
        var record = diff.Diff.GetRecord(detail.Record);
        if (detail.MirrorLeftElement < 0 || detail.MirrorRightOrdinal < 0)
            return null;

        if (record.ParentRecord < 0)
            return diff.Right.Root is { } root ? diff.Right.Path(root.ValueStart) : null;

        var parent = diff.Right.NodeAt(diff.Diff.GetRecord(record.ParentRecord).Right);
        foreach (var element in diff.Right.ChildrenFrom(parent, detail.MirrorRightOrdinal))
            return diff.Right.Path(element.ValueStart);
        return null;
    }

    /// <summary>
    /// The character-level diff behind the context bar's highlight: the longest common
    /// prefix and suffix bracket the span that actually differs. Inputs are the
    /// display-capped values (never wrapped, so a single differing span reads well);
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

    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

    /// <summary>Both file names, which is the one place in this document they are not a
    /// repetition: the toolbar and status bar describe what the comparison found.</summary>
    public string WindowTitle =>
        $"{AppInfo.Name} Diff ({Path.GetFileName(FilePath)} ↔ {Path.GetFileName(RightFilePath)})";

    /// <summary>
    /// Opens both files and starts the pipeline. Returns once the rows exist - the left document
    /// shows at once; indexing and the diff continue in the background, monitored for
    /// status/failure updates, and the merged tree takes over once it has rows.
    /// </summary>
    public Task LoadAsync(IByteOrigin leftOrigin, IByteOrigin rightOrigin)
    {
        Origin = leftOrigin;
        FilePath = leftOrigin.Path ?? leftOrigin.DisplayName;
        RightFilePath = rightOrigin.Path ?? rightOrigin.DisplayName;

        var board = ProgressBoard.Shared;
        var leftProgress = board.Begin("Indexing " + leftOrigin.DisplayName);
        var rightProgress = board.Begin("Indexing " + rightOrigin.DisplayName);
        var diffProgress = board.Begin($"Comparing {leftOrigin.DisplayName} with {rightOrigin.DisplayName}");

        JsonDiffSession started;
        try
        {
            started = JsonDiffSession.Start(leftOrigin, rightOrigin, leftProgress, rightProgress, diffProgress);
        }
        catch
        {
            // Nothing is running, so nothing will finish these.
            leftProgress.Finish();
            rightProgress.Finish();
            diffProgress.Finish();
            throw;
        }

        session = started;
        leftProgress.FinishWhen(started.Left.IndexingTask);
        rightProgress.FinishWhen(started.Right.IndexingTask);
        diffProgress.FinishWhen(started.IndexingTask);

        toolbar = new JsonDiffToolbarViewModel(
            setChangesOnly: SetChangesOnly,
            goToPreviousDiff: GoToPreviousDiff,
            goToNextDiff: GoToNextDiff);

        diffTree = new JsonDiffTree(started);

        // The preview reads the left document's bytes directly, so it needs nothing indexed.
        var leftDocument = started.LeftDocument;
        var leftBytes = leftDocument.Bytes;
        preview = new TreeDocument(leftDocument.Index.Structure, leftDocument.Reader,
            new JsonTreePainter(leftDocument.Text, hintProviders: null, offerArrayTable: false),
            new TreeExpandState(1), () => leftBytes.AvailableLength);

        // Sampled BEFORE choosing, for the reason a growth monitor attached to a finished task
        // is harmless and a missing one is not: a diff completing in between would leave the
        // preview up with nothing left to replace it.
        bool comparing = !started.Diff.AllItemsPublished;
        Tree = HasDiffRows(started) ? diffTree : preview;
        if (comparing)
            growthMonitor = new IndexGrowthMonitor(GrowthInterval, started.IndexingTask, () => started.Diff.AllItemsPublished, OnGrew);

        StatusText = $"Comparing {FilePath} with {RightFilePath}";
        MonitorIndexing();
        return Task.CompletedTask;
    }

    private static bool HasDiffRows(JsonDiffSession current) => current.Diff.RecordCount > 0 || current.Diff.AllItemsPublished;

    /// <summary>More has been indexed or compared: the preview gives way to the merged tree as
    /// soon as it has rows, and whichever is showing catches up.</summary>
    private void OnGrew()
    {
        if (IsDisposed || session is not { } current)
            return;

        if (!ReferenceEquals(Tree, diffTree) && HasDiffRows(current))
            Tree = diffTree;
        else if (ReferenceEquals(Tree, diffTree))
            diffTree!.NotifyGrew();
        else
            preview?.NotifyGrew();
    }

    /// <summary>
    /// Both completion hooks run the SAME reaction, which is what makes the diff different from
    /// every other document here. A failure on either side does not fault the diff's own task -
    /// the comparison completes normally over an empty index - so the side attribution below has
    /// to happen on the success path as well as the failure one. The base's
    /// failure is ignored for the same reason <see cref="JsonDiffSession.Failure"/> is always
    /// null: an unattributed failure would lose which file failed, and only this class knows the
    /// display names to attribute it with.
    /// </summary>
    protected override void OnIndexingCompleted() => ReportComparisonOutcome();

    /// <inheritdoc cref="OnIndexingCompleted"/>
    protected override void OnIndexingFailed(IndexFailure? failure) => ReportComparisonOutcome();

    private void ReportComparisonOutcome()
    {
        if (session is not { } current)
            return;

        // Attribute a side failure - the diff completes empty in that case, and the shell's
        // existing IndexFailure handling (banner or incompatible placeholder) takes over.
        if (current.Left.Index.Failure is { } leftFailure)
        {
            IndexFailure = new IndexFailure($"Left file: {leftFailure.Message}", leftFailure.ByteOffset, leftFailure.Line, leftFailure.Column, leftFailure.ItemsIndexed);
            StatusText = $"{FilePath} — left file failed to index";
            return;
        }

        if (current.Right.Index.Failure is { } rightFailure)
        {
            IndexFailure = new IndexFailure($"Right file: {rightFailure.Message}", rightFailure.ByteOffset, rightFailure.Line, rightFailure.Column, rightFailure.ItemsIndexed);
            StatusText = $"{RightFilePath} — right file failed to index";
            return;
        }

        if (!current.Diff.AllItemsPublished)
            return;

        OnGrew();
        StatusText = Summarize(current.Diff);
    }

    /// <summary>One pass over the finished record log, counting user-meaningful changes:
    /// whole-subtree adds/removes, modified leaves and ranges (a descended pair is structure,
    /// not itself a change), and move destinations.</summary>
    private static string Summarize(JsonDiffIndex diff)
    {
        int added = 0, removed = 0, modified = 0, moved = 0;
        for (int i = 0; i < diff.RecordCount; i++)
        {
            var record = diff.GetRecord(i);
            if (record.IsMovedWithin)
                moved++;

            switch (record.Status)
            {
                case DiffStatus.Added: added++; break;
                case DiffStatus.Removed: removed++; break;
                case DiffStatus.Moved when !record.IsMoveSource: moved++; break;
                case DiffStatus.Modified when !record.HasChildRecords: modified++; break;
            }
        }

        if (added + removed + modified + moved == 0)
            return "documents are identical";

        return $"{added:N0} added, {removed:N0} removed, {modified:N0} modified, {moved:N0} moved";
    }

    /// <summary>
    /// What the base disposes before releasing the session: the growth monitor stops, and every
    /// surface drawing either tree lets go of it, since another row drawn after the release would
    /// read an unmapped file.
    /// </summary>
    private sealed class CloseTrees(JsonDiffViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            owner.growthMonitor?.Dispose();
            owner.growthMonitor = null;
            owner.preview?.Close();
            owner.diffTree?.Close();
        }
    }
}
