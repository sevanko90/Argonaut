using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Hints;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Search;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Launching;
using Argonaut.Ui.Notifications;

namespace Argonaut.Ui.Tree;

/// <summary>
/// A document shown as a tree, whatever its format. The tree reads the bytes through a sparse
/// index (<see cref="ITreeIndex"/>) and a <see cref="TreeDocument"/> the view's surface draws;
/// nothing holds a record per token, and the tree can be shown before indexing has got anywhere.
/// Selection, reveals and path segments are byte offsets - the start of the row they name.
///
/// A format derives from this and supplies what is its own: the index it starts
/// (<see cref="StartSession"/>), the reader, painter and gutters the tree is built from
/// (<see cref="ShowTree"/>), how a row's ancestry is spelled as a path and a path is resolved
/// back to a row, and what copying a row's value puts on the clipboard.
/// </summary>
public abstract class TreeDocumentViewModel : IndexedDocumentViewModel, IPathNavigable, IByteRangeNavigable
{
    /// <summary>How often a still-indexing document tells its surface there is more to show.</summary>
    private static readonly TimeSpan GrowthInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How far into a row a current match is looked for: past what a row's text shows,
    /// so a match further in has nothing to draw.</summary>
    private const int CurrentMatchReach = 64 * 1024;

    private readonly ITreeViewSettings viewSettings;
    private readonly ISystemLauncher? launcher;

    private IndexedSourceSession<ITreeIndex>? session;
    private TreeDocument? tree;
    private TreeExpandState? expand;
    private IndexGrowthMonitor? growthMonitor;
    private TreeRow? selectedRow;
    private string? selectedPath;
    private string? selectedValueText;
    private string? highlightTerm;
    private bool showIndentGuides;
    private IReadOnlyList<TreePathSegment> selectedPathSegments = Array.Empty<TreePathSegment>();

    // The find match last revealed, until a reveal of anything else.
    private long? currentMatchOffset;

    // The offset a reveal is waiting for the index to reach, before it becomes PendingReveal. Kept
    // apart so a view attaching meanwhile finds nothing to show rather than seeking past the index.
    private long? revealAwaitingIndex;

    /// <param name="viewSettings">Where the default expand depth and indent guides are remembered.</param>
    /// <param name="launcher">Opens the links value hints offer; null opens none.</param>
    protected TreeDocumentViewModel(ITreeViewSettings viewSettings, ISystemLauncher? launcher)
    {
        this.viewSettings = viewSettings;
        this.launcher = launcher;
        showIndentGuides = viewSettings.ShowIndentGuides;
    }

    protected override IDocumentSession? Session => session;

    protected override IDisposable? MappedRows => growthMonitor is null && tree is null ? null : new CloseTree(this);

    /// <summary>The remembered tree settings this document was built with.</summary>
    protected ITreeViewSettings ViewSettings => viewSettings;

    /// <summary>The session indexing and reading this document, once <see cref="StartSession"/> has run.</summary>
    protected IndexedSourceSession<ITreeIndex>? TreeSession => session;

    internal IByteSource? Bytes => session?.Bytes;

    /// <summary>
    /// What a find should scan for this document: the whole file for a top-level load, or just a
    /// byte range for a document nested in another (an NDJSON line's tree). Carrying the range
    /// matters - a nested document's offsets are zero-based at its start, so a whole-file scan
    /// would report offsets it cannot resolve (see ScanTarget).
    /// </summary>
    internal ScanTarget ScanTarget { get; private protected set; }

    /// <summary>Fires when this document begins tearing down, for
    /// <c>ISearchNavigator.DocumentTearingDown</c> - a find reveal links it so it stops
    /// rather than touching a released mapping.</summary>
    internal CancellationToken TearingDown => session?.TearingDown ?? default;

    /// <summary>The tree the view's surface draws. Null until <see cref="ShowTree"/> has run.</summary>
    public TreeDocument? Tree => tree;

    /// <summary>
    /// How many container levels are expanded by default. Set before the load to affect the
    /// initial view; <see cref="SetDefaultExpandDepth"/> changes it afterwards.
    /// </summary>
    public int DefaultExpandDepth { get; set; } = 2;

    /// <summary>The selected row, or null.</summary>
    public TreeRow? SelectedRow
    {
        get => selectedRow;
        private set => SetField(ref selectedRow, value);
    }

    /// <summary>The selected row's path, spelled in the format's own syntax; null with no selection.</summary>
    public string? SelectedPath
    {
        get => selectedPath;
        private set => SetField(ref selectedPath, value);
    }

    /// <summary>The selected row's path as clickable pieces, each naming the row it reveals.</summary>
    public IReadOnlyList<TreePathSegment> SelectedPathSegments
    {
        get => selectedPathSegments;
        private set => SetField(ref selectedPathSegments, value);
    }

    /// <summary>The selected row's value as copy-value puts it on the clipboard.</summary>
    public string? SelectedValueText
    {
        get => selectedValueText;
        private set => SetField(ref selectedValueText, value);
    }

    /// <summary>Whether the tree draws indent guides - the remembered choice, or the owning
    /// document's for a nested tree.</summary>
    public bool ShowIndentGuides
    {
        get => showIndentGuides;
        set => SetField(ref showIndentGuides, value);
    }

    /// <summary>
    /// The active find term; rows highlight it in their displayed text. Null when no find is
    /// active.
    /// </summary>
    public string? HighlightTerm
    {
        get => highlightTerm;
        set => SetField(ref highlightTerm, value);
    }

    /// <summary>
    /// The offset a reveal is waiting to show - set by <see cref="Reveal"/> once the index covers
    /// it, and consumed by the view, which may not exist yet when it is asked for (a search reveal
    /// into an NDJSON line selects the line first, and its view arrives a moment later; a view
    /// switch reveals before the new view is laid out).
    /// </summary>
    public long? PendingReveal { get; private set; }

    /// <summary>A reveal was asked for; the view shows <see cref="PendingReveal"/>.</summary>
    public event EventHandler? RevealRequested;

    /// <summary>What rows say changed - a schema bound, a hint setting - though the rows did not.</summary>
    public event EventHandler? RowsInvalidated;

    /// <summary>The default expand depth changed: the rows on screen are different rows.</summary>
    public event EventHandler? ExpansionReset;

    /// <summary>Tells the view the rows' appearance changed though their bytes did not.</summary>
    protected void InvalidateRows() => RowsInvalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>The path to the cursor's row, a segment per level; a close row's path is its
    /// container's.</summary>
    protected abstract IReadOnlyList<TreePathSegment> PathSegmentsOf(TreeCursor cursor);

    /// <summary>The segments spelled as the one path string copy-path puts on the clipboard.</summary>
    protected abstract string FormatPath(IReadOnlyList<TreePathSegment> segments);

    /// <summary>The row's value as copy-value puts it on the clipboard.</summary>
    protected abstract string ValueTextOf(in TreeRow row);

    /// <summary>
    /// Resolves <paramref name="path"/> to the row it names, reading down from the root. Runs on
    /// the pool as a dependent read of the session, so it may read the bytes freely.
    /// </summary>
    protected abstract TreePathResult ResolvePath(SparseContainerIndex structure, string path);

    /// <summary>
    /// Starts indexing <paramref name="bytes"/> and keeps the session that now owns them. Called
    /// first in a load, so a failure later in it still leaves the session for the base to release.
    /// </summary>
    protected IndexedSourceSession<ITreeIndex> StartSession(IByteSource bytes,
        Func<IByteSource, IProgressReporter?, CancellationToken, ITreeIndex> startIndexing, IProgressReporter? progressReporter)
    {
        var started = IndexedSourceSession<ITreeIndex>.Start(bytes, startIndexing, progressReporter);
        session = started;
        return started;
    }

    /// <summary>
    /// Builds the tree over the session's structure from the format's reader, painter and gutters,
    /// and starts the growth monitor if indexing is still going. The tree reads the bytes
    /// directly, so it is shown at once: the index only speeds up jumps, and grows underneath it.
    /// </summary>
    protected TreeDocument ShowTree(ITreeFormatReader reader, ITreeRowPainter painter, IReadOnlyList<ITreeGutter> gutters)
    {
        var current = session!;
        var sourceBytes = current.Bytes;
        expand = new TreeExpandState(DefaultExpandDepth);
        tree = new TreeDocument(current.Index.Structure, reader, painter, expand, () => sourceBytes.AvailableLength, gutters);

        if (!current.Index.AllItemsPublished)
        {
            var index = current.Index;
            growthMonitor = new IndexGrowthMonitor(GrowthInterval, index.IndexingTask, () => index.AllItemsPublished,
                () => tree?.NotifyGrew());
        }

        return tree;
    }

    /// <summary>Records what the load opens: the origin, its display text, and what a find scans.</summary>
    protected void AdoptOrigin(IByteOrigin origin, ScanTarget scanTarget)
    {
        Origin = origin;
        FilePath = origin.Path ?? origin.DisplayName;
        ScanTarget = scanTarget;
    }

    /// <summary>
    /// Asks the view to select and show the row holding <paramref name="offset"/>, expanding
    /// whatever hides it. An offset the index has not reached yet - a search hit racing ahead of
    /// a multi-GB scan - waits for it: seeking there sooner would read every sibling from the
    /// last indexed point to the target on the UI thread.
    /// </summary>
    public void Reveal(long offset) => Reveal(offset, isMatch: false);

    /// <summary>Reveals a find match, which the tree then draws as the current one (see
    /// <see cref="CurrentMatchIn"/>).</summary>
    public void RevealMatch(long offset) => Reveal(offset, isMatch: true);

    private void Reveal(long offset, bool isMatch)
    {
        // Set first: the view may show the reveal before this returns.
        currentMatchOffset = isMatch ? offset : null;
        if (IsCovered(offset))
        {
            ShowWhenReady(offset);
            return;
        }

        PendingReveal = null;
        revealAwaitingIndex = offset;
        _ = RevealWhenCoveredAsync(offset);
    }

    /// <summary>
    /// Which of the find term's occurrences in <paramref name="row"/>'s text is the match last
    /// revealed: the occurrences in the row's bytes before it, counted as the tree counts them in
    /// its text. Null when the row does not hold it, or holds it further in than a row shows.
    /// </summary>
    public TreeCurrentMatch? CurrentMatchIn(TreeRow? row)
    {
        if (currentMatchOffset is not { } offset || row is not { } shown || string.IsNullOrEmpty(HighlightTerm) || Bytes is not { } bytes)
            return null;

        long before = offset - shown.Node.RowStart;
        if (before < 0 || before > CurrentMatchReach)
            return null;

        string preceding = Encoding.UTF8.GetString(bytes.RequireContiguous(shown.Node.RowStart, (int)before));
        int occurrence = 0;
        for (int at = preceding.IndexOf(HighlightTerm, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = preceding.IndexOf(HighlightTerm, at + HighlightTerm.Length, StringComparison.OrdinalIgnoreCase))
        {
            occurrence++;
        }

        return new TreeCurrentMatch(shown.Key, occurrence);
    }

    /// <summary>Opens a value hint's address with the system's handler for it. Only the schemes
    /// <see cref="UrlHintProvider.IsSafe"/> allows go out: the address comes from the document,
    /// and a document must never be able to launch a file or a custom URL scheme.</summary>
    public void OpenAddress(Uri address)
    {
        if (UrlHintProvider.IsSafe(address))
            launcher?.OpenAddress(address);
    }

    private void ShowWhenReady(long offset)
    {
        revealAwaitingIndex = null;
        PendingReveal = offset;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsCovered(long offset)
        => session is not { } current || current.Index.AllItemsPublished
           || current.Index.Structure.IsComplete || offset < current.Index.Structure.ScannedTo;

    private async Task RevealWhenCoveredAsync(long offset)
    {
        var current = session!;
        try
        {
            while (!IsCovered(offset))
                await Task.Delay(100, current.TearingDown);
        }
        catch (OperationCanceledException)
        {
            return; // the document closed while waiting
        }

        // A later reveal replaced this one while it waited.
        if (!IsDisposed && revealAwaitingIndex == offset)
            ShowWhenReady(offset);
    }

    /// <summary>The view has shown <see cref="PendingReveal"/>.</summary>
    public void ClearPendingReveal() => PendingReveal = null;

    /// <summary>
    /// The selected node's own bytes, relative to the input rather than to this document's
    /// source (an NDJSON line's tree is offset by its line start): the node as written, from its
    /// first byte to its last. A closing row stands for the container it closes. A container
    /// whose end is not known cheaply is only a position (see <see cref="KnownEnd"/>).
    /// </summary>
    public ByteRange? SelectedByteRange
    {
        get
        {
            if (SelectedRow is not { } row || session is not { } current || tree is null)
                return null;

            var node = row.Node;
            long start = ScanTarget.Offset + node.ValueStart;
            return KnownEnd(current.Index.Structure, tree.Reader, node) is { } end
                ? new ByteRange(start, end - node.ValueStart)
                : ByteRange.At(start);
        }
    }

    /// <summary>
    /// Where <paramref name="node"/> ends, when that costs a bounded read: a scalar's recorded end,
    /// a recorded container's end once it has closed, or - for a container the scan has gone far
    /// enough past to have recorded were it large - a scan of fewer than
    /// <see cref="SparseContainerIndex.PromotionBytes"/>. Null for a container still open, or one
    /// the scan has not reached, whose end could be gigabytes away.
    /// </summary>
    private static long? KnownEnd(SparseContainerIndex structure, ITreeFormatReader reader, TreeNode node)
    {
        if (!node.IsContainer)
            return node.ValueEnd;

        int record = structure.FindContainerStartingAt(node.ValueStart);
        if (record >= 0)
            return structure.GetContainer(record).End is var end and >= 0 ? end : null;

        bool small = structure.IsComplete || node.ValueStart + structure.PromotionBytes <= structure.ScannedTo;
        return small ? reader.SkipValue(node.ValueStart) : null;
    }

    /// <summary>
    /// Selects the row <paramref name="range"/> starts in, exactly as a search hit there would:
    /// bytes between nodes (a property name, punctuation) resolve to the value they lead up to,
    /// and a closing bracket to its container's closing row. A range outside this document's
    /// bytes (another line of an NDJSON file) reveals nothing.
    /// </summary>
    public Task RevealByteRangeAsync(ByteRange range)
    {
        long offset = range.Offset - ScanTarget.Offset;
        if (session is null || IsDisposed || offset < 0 || (ScanTarget.Length >= 0 && offset > ScanTarget.Length))
            return Task.CompletedTask;

        Reveal(offset);
        return Task.CompletedTask;
    }

    /// <summary>Asks the shell to show the selected node in the raw text view, through
    /// <see cref="RawJumpService"/> - the view model never learns the shell exists.</summary>
    public void ShowSelectionInText()
    {
        if (SelectedByteRange is { } range)
            RawJumpService.Request(range);
    }

    /// <summary>
    /// The view's selection moved to <paramref name="row"/>: works out its path and its value.
    /// Only the row's ancestry is read, so this costs the same anywhere in any document.
    /// </summary>
    public void OnRowSelected(TreeRow? row)
    {
        SelectedRow = row;
        if (row is not { } selected || session is null || tree is null)
        {
            SelectedPath = null;
            SelectedPathSegments = Array.Empty<TreePathSegment>();
            SelectedValueText = null;
            return;
        }

        var cursor = tree.NewCursor();
        cursor.SeekTo(selected.Start);
        var segments = PathSegmentsOf(cursor);
        SelectedPathSegments = segments;
        SelectedPath = FormatPath(segments);
        SelectedValueText = ValueTextOf(selected);
    }

    /// <summary>
    /// Resolves a path string and reveals the target, or surfaces a toast on parse/lookup
    /// failure. Wired into the path bar's "Go to path" action. Runs as a dependent read so closing
    /// the document mid-resolve joins it before the mapping is released.
    /// </summary>
    public async Task NavigateToPathAsync(string path)
    {
        if (session is null || IsDisposed || tree is null)
        {
            ToastService.Show("No file loaded yet.");
            return;
        }

        var current = session;
        var structure = current.Index.Structure;
        TreePathResult result;
        try
        {
            result = await current.StartDependentRead(_ => Task.Run(() => ResolvePath(structure, path)));
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                ToastService.Show($"Navigation failed: {ex.Message}");
            return;
        }

        if (IsDisposed)
            return;

        if (result.Target is { } target)
            Reveal(target);
        else
            ToastService.Show(result.Error ?? "Path not found.");
    }

    /// <summary>
    /// Changes the default-expand depth and applies it immediately if a file is already loaded,
    /// in addition to affecting future loads.
    /// </summary>
    public void SetDefaultExpandDepth(int depth)
    {
        DefaultExpandDepth = depth;
        if (expand is null)
            return;

        expand.DefaultDepth = depth;
        expand.Reset();
        ExpansionReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The toolbar's expand-depth choice: remembered for the next document, then applied
    /// to this one.</summary>
    protected void ChooseExpandDepth(int depth)
    {
        viewSettings.ExpandDepth = depth;
        SetDefaultExpandDepth(depth);
    }

    /// <summary>The toolbar's indent-guides choice: remembered for the next document, then
    /// applied to this one.</summary>
    protected void ChooseIndentGuides(bool show)
    {
        viewSettings.ShowIndentGuides = show;
        ShowIndentGuides = show;
    }

    /// <summary>
    /// What the base disposes before releasing the session: the growth monitor stops, and every
    /// surface drawing the tree lets go of it, since another row drawn after the release would
    /// read an unmapped file.
    /// </summary>
    private sealed class CloseTree(TreeDocumentViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            owner.growthMonitor?.Dispose();
            owner.growthMonitor = null;
            owner.tree?.Close();
        }
    }
}
