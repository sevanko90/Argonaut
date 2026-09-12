using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Features.Raw;

/// <summary>
/// Document view model for the raw viewer - the fallback for files no other viewer claims
/// (<see cref="FileTypeDetector.FileKind.Unidentified"/>). Interprets nothing: the file is
/// indexed into cap-bounded display rows by <see cref="RawSegmentIndex"/> and shown as-is.
///
/// The wrap-width change (<see cref="SetWrapWidth"/>) re-indexes over the SAME mapping via
/// <see cref="RawIndexSession.RestartIndex"/> - see RawIndexSession for why the mapping must
/// survive (a live search scan may hold spans over it). The rows collection is swapped as a
/// whole new instance rather than reset in place, so the ListBox rebinds cleanly and the
/// disposed old collection reports empty for Avalonia's trailing ItemsSource walk.
/// </summary>
public sealed class RawViewModel : IndexedDocumentViewModel, IByteOffsetNavigable
{
    private const int InitialIndexedRowTarget = 250;

    private RawIndexSession? session;
    private RawRowCollection? rows;
    private RawToolbarViewModel? toolbar;
    private string? highlightTerm;
    private int? selectedRowIndex;
    private RawCaretController? caret;
    private RawCaretReadout? caretReadout;
    private int wrapWidth = RawWrapWidthPreference.Default;

    protected override IDocumentSession? Session => this.session;

    protected override IDisposable? MappedRows => this.rows;

    internal RawSegmentIndex? Index => this.session?.Index;

    internal IByteSource? Bytes => this.session?.Bytes;

    /// <summary>Fires when this document begins tearing down, for
    /// <see cref="ISearchNavigator.DocumentTearingDown"/>. Deliberately the mapping-lifetime
    /// source, not the (recycled, per-restart) index one - see
    /// <see cref="RawIndexSession.TearingDown"/>.</summary>
    internal CancellationToken TearingDown => this.session?.TearingDown ?? default;

    public int RowCount => this.session?.Index.RowCount ?? 0;

    /// <summary>Byte cap per display row. Observable so the view can recompute its pan range.</summary>
    public int WrapWidth
    {
        get => this.wrapWidth;
        private set => SetField(ref this.wrapWidth, value);
    }

    /// <summary>
    /// Bumped each time <see cref="SetWrapWidth"/> replaces the index, so an in-flight search
    /// reveal can detect that it resolved against a retired index and re-resolve.
    /// </summary>
    public int IndexGeneration { get; private set; }

    public RawRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    /// <summary>
    /// The caret and selection. Null until <see cref="LoadAsync"/> has an index to move over.
    /// Replaced by a wrap-width change (the row index it moves over is), but the caret's own
    /// position survives it untouched: a caret is a byte offset, and re-wrapping moves rows
    /// around without moving a single byte.
    /// </summary>
    public RawCaretController? Caret
    {
        get => this.caret;
        private set
        {
            if (this.caret is not null)
                this.caret.Moved -= OnCaretMoved;

            SetField(ref this.caret, value);

            if (this.caret is not null)
                this.caret.Moved += OnCaretMoved;

            RefreshCaretReadout();
        }
    }

    /// <summary>
    /// What the status gutter shows: the character under the caret, where it is, and how much is
    /// selected. Null before there is a document, and while the caret sits on a byte the scan has
    /// not reached yet.
    /// </summary>
    public RawCaretReadout? CaretReadout
    {
        get => this.caretReadout;
        private set => SetField(ref this.caretReadout, value);
    }

    /// <summary>The character under the caret, e.g. "U+2028 LINE SEPARATOR".</summary>
    public string CaretCharacterText => CaretReadout?.Character ?? string.Empty;

    /// <summary>Byte offset and line/column, the latter omitted when it could not be answered
    /// cheaply - see <see cref="RawCaretReadout.ColumnScanBytes"/>.</summary>
    public string CaretPositionText
    {
        get
        {
            if (CaretReadout is not { } readout)
                return string.Empty;

            string position = $"Byte {readout.ByteOffset:N0}";
            if (readout.LineNumber is int line)
            {
                position += readout.Column is int column
                    ? $"    Ln {line:N0}, Col {column:N0}"
                    : $"    Ln {line:N0}, Col —";
            }

            return position;
        }
    }

    /// <summary>Selection size, empty when nothing is selected. Bytes always; characters when the
    /// selection is small enough to decode (<see cref="RawCaretReadout.SelectionScanBytes"/>).</summary>
    public string CaretSelectionText
    {
        get
        {
            if (CaretReadout is not { SelectionBytes: > 0 } readout)
                return string.Empty;

            string bytes = $"{readout.SelectionBytes:N0} {(readout.SelectionBytes == 1 ? "byte" : "bytes")}";
            return readout.SelectionCharacters is long characters
                ? $"Selected {bytes} ({characters:N0} {(characters == 1 ? "char" : "chars")})"
                : $"Selected {bytes}";
        }
    }

    private void OnCaretMoved(object? sender, EventArgs e) => RefreshCaretReadout();

    /// <summary>
    /// Re-reads the document around the caret. Called on every caret move, which is why
    /// <see cref="RawCaretReadout.Describe"/> is bounded rather than exact.
    /// </summary>
    private void RefreshCaretReadout()
    {
        CaretReadout = this.caret is null || this.session is null
            ? null
            : RawCaretReadout.Describe(
                this.session.Index, this.session.Bytes, this.caret.Caret, this.caret.Selection);

        OnPropertyChanged(nameof(CaretCharacterText));
        OnPropertyChanged(nameof(CaretPositionText));
        OnPropertyChanged(nameof(CaretSelectionText));
    }

    /// <summary>
    /// Whether <see cref="Rows"/> is safe to read yet. A view is attached before
    /// <see cref="LoadAsync"/> finishes, and it needs to subscribe to the row set's growth
    /// notifications the moment there is one - so it needs to be able to ask rather than to
    /// catch the exception the property throws.
    /// </summary>
    public bool HasRows => this.rows is not null;

    public override object? Toolbar => this.toolbar;

    /// <summary>The active find term, highlighted in every visible row via RawView's
    /// SearchHighlight bindings.</summary>
    public string? HighlightTerm
    {
        get => this.highlightTerm;
        set => SetField(ref this.highlightTerm, value);
    }

    /// <summary>Row index a search reveal wants scrolled/selected into view; the view mirrors
    /// it into the ListBox selection.</summary>
    public int? SelectedRowIndex
    {
        get => this.selectedRowIndex;
        private set => SetField(ref this.selectedRowIndex, value);
    }

    /// <summary>Used by RawSearchNavigator to reveal a search match.</summary>
    public void SelectRow(int rowIndex) => SelectedRowIndex = rowIndex;

    /// <summary>
    /// Reveals a byte offset: centres its row in the viewport and puts the caret on it. Both
    /// halves matter - scrolling somewhere without moving the caret leaves the next keystroke
    /// acting on wherever the caret was last, which after a jump across a multi-GB file is
    /// nowhere near what the user is now looking at.
    /// </summary>
    public void RevealOffset(long byteOffset, int rowIndex)
    {
        // Reveal BEFORE placing the caret, and not the other way round. Moving the caret scrolls
        // it into view by the shortest distance, which parks the row against the bottom edge -
        // and the centred reveal that follows then finds it already on screen and leaves it
        // there. Ordering is load-bearing here, which is why the jump is tested end to end
        // rather than by calling the surface's reveal directly.
        SelectRow(rowIndex);
        Caret?.PlaceAt(byteOffset);
    }

    /// <summary>
    /// Resolves <paramref name="byteOffset"/> to a display row - waiting for indexing to reach
    /// it (or finish) if necessary - and reveals it. Used by the "jump to failure location"
    /// link on another document's incompatible/partial-failure display, which switches this
    /// view in then calls here. Mirrors <see cref="Argonaut.Features.Search.RawSearchNavigator.RevealAsync"/>'s
    /// generation re-check for a wrap-width change racing the resolve, and its own disposal:
    /// if the document is closed/switched away while resolving, resuming touches an
    /// already-unmapped file, which surfaces as a catchable <see cref="ObjectDisposedException"/>
    /// (see CLAUDE.md/MMapFile) rather than corrupting anything - simply ignored here since
    /// there is nothing left to reveal.
    /// </summary>
    public async Task JumpToByteOffsetAsync(long byteOffset)
    {
        if (this.session is null)
            return;

        try
        {
            int generation = IndexGeneration;
            var row = await RawOffsetRowResolver.ResolveWhenCoveredAsync(this.session.Index, byteOffset, CancellationToken.None);
            if (this.IsDisposed)
                return;

            if (generation != IndexGeneration)
            {
                row = await RawOffsetRowResolver.ResolveWhenCoveredAsync(this.session!.Index, byteOffset, CancellationToken.None);
                if (this.IsDisposed)
                    return;
            }

            if (row is int rowIndex)
                RevealOffset(byteOffset, rowIndex);
        }
        catch (ObjectDisposedException)
        {
            // Document closed/switched away mid-resolve; the mapping may already be gone.
        }
    }

    public async Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        this.FilePath = path;
        this.wrapWidth = RawWrapWidthPreference.Load();
        this.toolbar = new RawToolbarViewModel(this.wrapWidth, SetWrapWidth);

        var session = RawIndexSession.Start(new MMapFile(path), this.wrapWidth, progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't an empty list; RowCount then
        // tracks the published row count live as indexing continues in the background.
        await session.Index.WaitForRowCountAsync(InitialIndexedRowTarget);

        if (session.Index.Failure is { } failure)
            IndexFailure = failure;

        this.rows = new RawRowCollection(session.Index, session.Bytes);
        Caret = new RawCaretController(session.Index, session.Bytes);

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{path} — {RowCount:N0} rows indexed so far";
        MonitorIndexing();
    }

    /// <summary>
    /// Applies a new wrap width by re-indexing the same mapping. Synchronous and entirely
    /// internal: a running search is unaffected (matches are byte offsets over the unchanged
    /// file), and the growth timer of the fresh collection fills rows in within ~120ms.
    /// </summary>
    public void SetWrapWidth(int bytes)
    {
        if (this.IsDisposed || this.session is null || bytes == this.wrapWidth)
            return;

        // Raised BEFORE the Rows swap below - the view reacts by resetting its scroll and
        // re-laying-out against the old collection, so the virtualizer's remembered viewport
        // is back at the top when the new ItemsSource arrives (see RawView's
        // ResetScrollBeforeSourceSwap). Reordering this method breaks that contract.
        WrapWidth = bytes;
        IndexGeneration++;

        // Clear selection before the swap - stale indexes must never be applied to the new list.
        SelectedRowIndex = null;

        // Stale relative to the new scan about to start; MonitorIndexing repopulates it if the
        // new scan fails.
        IndexFailure = null;

        this.session.RestartIndex(bytes);

        var old = this.rows;
        this.rows = new RawRowCollection(this.session.Index, this.session.Bytes);
        old?.Dispose();

        // A byte offset means the same thing at any wrap width, so the caret carries across the
        // re-index; only the row index it consults is replaced.
        long caretOffset = Caret?.Caret.Offset ?? 0;
        var selection = Caret?.Selection ?? default;
        Caret = new RawCaretController(this.session.Index, this.session.Bytes);
        if (selection.IsEmpty)
        {
            Caret.PlaceAt(caretOffset);
        }
        else
        {
            // Anchor first, then extend, so a selection made right-to-left keeps its direction.
            Caret.PlaceAt(selection.Anchor);
            Caret.ExtendTo(selection.Active);
        }

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{FilePath} — {RowCount:N0} rows indexed so far";
        MonitorIndexing();
    }

    public override ISearchNavigator? CreateSearchNavigator() => new RawSearchNavigator(this);

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Unidentified;
    }

    /// <summary>Indexing finished: reports <see cref="RowCount"/> under its real total. The
    /// base's ReferenceEquals(indexer, accessor()) guard is what
    /// <see cref="Argonaut.Features.Raw.RawViewModel.IndexGeneration"/>'s remarks call out as
    /// covering a wrap-width restart retiring this index mid-monitor.</summary>
    protected override void OnIndexingCompleted()
        => StatusText = $"{FilePath} — {RowCount:N0} rows";

    /// <summary>Indexing stopped early (failure, or cancellation on <paramref name="failure"/> null).</summary>
    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        IndexFailure = failure;
        StatusText = failure is { } f
            ? $"{FilePath} — indexing stopped — {f.ItemsIndexed:N0} rows shown"
            : $"{FilePath} — indexing failed";
    }
}
