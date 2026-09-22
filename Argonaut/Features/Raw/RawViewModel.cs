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
public sealed class RawViewModel : IndexedDocumentViewModel, IByteOffsetNavigable, ISaveableDocument
{
    private const int InitialIndexedRowTarget = 250;

    private RawIndexSession? session;

    /// <summary>The session's bytes, held as what they are so a save can unmap and remap them -
    /// see <see cref="RemappableByteSource"/>.</summary>
    private RemappableByteSource? fileBytes;
    private RawRowCollection? rows;
    private RawToolbarViewModel? toolbar;
    private string? highlightTerm;
    private int? selectedRowIndex;
    private RawCaretController? caret;
    private RawCaretReadout? caretReadout;
    private RawEditController? editor;
    private bool isEditing;
    private bool isSaving;

    /// <summary>The background half of a save - the copy into the stage - which disposal must
    /// stop and join before the session releases the mapping it reads.</summary>
    private Task saveCopy = Task.CompletedTask;

    private CancellationTokenSource? saveCts;

    /// <summary>Progress for a re-index this document started itself - a wrap-width change, or
    /// the reopen after a save. The shell reports the first load; its reporter has stopped by the
    /// time either of these can happen.</summary>
    private StatusLineProgress? reindexProgress;

    private int wrapWidth = RawWrapWidthPreference.Default;

    protected override IDocumentSession? Session => this.session;

    protected override IDisposable? MappedRows => this.rows;

    /// <summary>
    /// The background scan over the file <i>as it is on disk</i>. Stays the scan even while the
    /// document is edited, because that is what the byte offsets a search produces are in - see
    /// <see cref="RowIndex"/> for what the view reads.
    /// </summary>
    internal RawSegmentIndex? Index => this.session?.Index;

    /// <summary>The file's bytes, unedited. <see cref="Document"/> is what is on screen.</summary>
    internal IByteSource? Bytes => this.session?.Bytes;

    /// <summary>
    /// The bytes the view shows: the file, or the piece table over it once editing has begun.
    /// Every reader that renders, decodes, measures or copies goes through this rather than
    /// <see cref="Bytes"/>.
    /// </summary>
    internal IByteSource? Document => this.editor?.Document ?? this.session?.Bytes;

    /// <summary>The rows of <see cref="Document"/>.</summary>
    internal IRawRowIndex? RowIndex => (IRawRowIndex?)this.editor?.RowIndex ?? this.session?.Index;

    /// <summary>Fires when this document begins tearing down, for
    /// <see cref="ISearchNavigator.DocumentTearingDown"/>. Deliberately the mapping-lifetime
    /// source, not the (recycled, per-restart) index one - see
    /// <see cref="RawIndexSession.TearingDown"/>.</summary>
    internal CancellationToken TearingDown => this.session?.TearingDown ?? default;

    public int RowCount => RowIndex?.RowCount ?? 0;

    /// <summary>Byte cap per display row. Observable so the view can recompute its pan range.</summary>
    public int WrapWidth
    {
        get => this.wrapWidth;
        private set => SetField(ref this.wrapWidth, value);
    }

    /// <summary>
    /// Bumped each time the index is replaced - by <see cref="SetWrapWidth"/>, or by a save
    /// reopening the document - so an in-flight search reveal can detect that it resolved against
    /// a retired index and re-resolve. Observable, so the view resets its scroll.
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


    // ---- editing ------------------------------------------------------------------------
    //
    // The raw view is where editing lives, because its row index is a pure function of the
    // bytes and can be re-derived where the JSON index cannot. Nothing here writes to the file;
    // only SaveAsync, below, does.

    /// <summary>
    /// True while keystrokes change the document rather than only moving the caret. A mode
    /// rather than an always-on editor because this is the viewer of last resort: it is what a
    /// user opens a 4GB file in to look at it, and a stray keypress that silently altered such a
    /// document would be the worst possible default.
    /// </summary>
    public bool IsEditing
    {
        get => this.isEditing;
        private set => SetField(ref this.isEditing, value);
    }

    /// <summary>
    /// Whether edit mode can be entered: the scan must have finished. The append log is read
    /// lock-free because nothing already written ever changes, and layering a mutating
    /// coordinate system over a log still being appended to would end that
    /// (<see cref="RawEditedRowIndex"/> refuses outright).
    /// </summary>
    public bool CanEdit => !IsDisposed && this.session is { } open && RawEditController.CanEdit(open.Index);

    /// <summary>True once the document reads differently from the file on disk.</summary>
    public bool IsDirty => this.editor?.IsDirty == true;

    /// <summary>See <see cref="ISaveableDocument.HasUnsavedChanges"/>; the same as
    /// <see cref="IsDirty"/>, under the name the shell asks by.</summary>
    public bool HasUnsavedChanges => IsDirty;

    /// <summary>True while a save is writing the document out. Edits, re-wrapping and the edit
    /// toggle are all refused meanwhile: the copy is reading the piece table on the background,
    /// and the piece table is not safe to change under a reader.</summary>
    public bool IsSaving
    {
        get => this.isSaving;
        private set
        {
            if (SetField(ref this.isSaving, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>See <see cref="ISaveableDocument.CanSave"/>. Waits for the scan for the same
    /// reason editing does, and because the save reopens the document over a fresh one.</summary>
    public bool CanSave => !IsSaving && CanEdit;

    /// <summary>
    /// Bumped by every edit, so the view drops the row text, layouts and decode maps it is
    /// holding. Separate from <see cref="IndexGeneration"/> because the two want different
    /// responses: a wrap change replaces the row collection wholesale, an edit keeps it.
    /// </summary>
    public int EditGeneration { get; private set; }

    /// <summary>What the status gutter says about editing: nothing, or that there are unsaved
    /// changes.</summary>
    public string EditStatusText => IsDirty ? "Edited — not saved" : string.Empty;

    /// <summary>
    /// Turns edit mode on or off. Turning it on for the first time builds the piece table and
    /// swaps the view onto it; turning it off leaves any edits in place - they are the
    /// document now, and discarding them silently is not an option the toggle gets to take.
    /// An edit-mode visit that changed nothing is undone completely, so the wrap-width combo
    /// (which is locked while a piece table exists) comes back.
    /// </summary>
    public void SetEditing(bool editing)
    {
        if (IsDisposed || this.session is null || editing == IsEditing)
            return;

        if (IsSaving)
        {
            ToastService.Show("Wait for the save to finish.");
            SyncToolbarEditing();
            return;
        }

        if (editing)
        {
            if (!CanEdit)
            {
                ToastService.Show("Wait for indexing to finish before editing.");
                SyncToolbarEditing();
                return;
            }

            if (this.editor is null)
                BeginEditing();
        }
        else if (this.editor is { IsDirty: false })
        {
            EndEditingUntouched();
        }

        IsEditing = editing;
        SyncToolbarEditing();
    }

    /// <summary>
    /// The editor, once edit mode has built one. For the Debug-only internals inspector, which
    /// is the only thing outside this class that has any business seeing it - everything else
    /// goes through <see cref="Document"/>, <see cref="RowIndex"/> and the methods below.
    /// </summary>
    internal RawEditController? Editor => this.editor;

    /// <summary>Types text at the caret. Returns false when nothing happened, so the view can
    /// leave the key for whoever else wants it.</summary>
    public bool TypeText(string text) => EditsPaused() || Report(this.editor?.Type(text));

    /// <summary>Inserts a line break at the caret.</summary>
    public bool InsertNewLine() => EditsPaused() || Report(this.editor?.InsertNewLine());

    /// <summary>
    /// Inserts raw bytes at the caret - what a paste is. Bytes rather than a string, so a
    /// clipboard that offers UTF-8 directly reaches the document without a decode-and-re-encode
    /// round trip that would silently repair anything invalid in it.
    /// </summary>
    public bool Paste(ReadOnlySpan<byte> bytes) => EditsPaused() || Report(this.editor is null ? null : this.editor.Insert(bytes));

    /// <summary>Backspace.</summary>
    public bool DeleteBackward() => EditsPaused() || Report(this.editor?.DeleteBackward());

    /// <summary>Forward delete.</summary>
    public bool DeleteForward() => EditsPaused() || Report(this.editor?.DeleteForward());

    public bool Undo() => EditsPaused() || Report(this.editor?.Undo());

    public bool Redo() => EditsPaused() || Report(this.editor?.Redo());

    /// <summary>True - so the key counts as handled and goes nowhere else - when a save is
    /// running and the edit must not happen. See <see cref="IsSaving"/>.</summary>
    private bool EditsPaused()
    {
        if (!IsSaving || this.editor is null)
            return false;

        ToastService.Show("Saving — editing resumes when it finishes.");
        return true;
    }

    private bool Report(RawEditOutcome? outcome)
    {
        switch (outcome)
        {
            case RawEditOutcome.Applied:
                return true;

            case RawEditOutcome.NoRoomForAnotherEditSite:
                // Each place edited costs the row index a span of re-derived rows. Until a
                // re-index over the piece table exists, opening more past its budget is refused
                // rather than the app quietly allocating its way onwards. Editing where changes
                // have already been made still works.
                ToastService.Show("Too many separate places edited to keep track of. Save or undo some first.");
                return true;

            default:
                return false;
        }
    }

    private void BeginEditing()
    {
        var session = this.session!;
        var editor = new RawEditController(session.Index, session.Bytes);
        editor.Changed += OnDocumentEdited;
        this.editor = editor;

        // The caret the editor built is over the piece table; the one being replaced was over
        // the file. A byte offset means the same thing in both while nothing has been edited, so
        // the position carries across.
        long caretOffset = Caret?.Caret.Offset ?? 0;
        var selection = Caret?.Selection ?? default;

        SwapRows(new RawRowCollection(editor.RowIndex, editor.Document));
        Caret = editor.Caret;
        RestoreCaret(caretOffset, selection);

        // A piece table pins the wrap width: re-indexing it would mean a fresh scan over edited
        // bytes, which is the same background re-index a rebuild needs and is not built.
        if (this.toolbar is { } toolbar)
            toolbar.CanChangeWrapWidth = false;

        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(EditStatusText));
    }

    /// <summary>Leaving edit mode having changed nothing puts the view back on the file itself,
    /// so nothing downstream has to reason about an idle piece table.</summary>
    private void EndEditingUntouched()
    {
        var session = this.session!;
        this.editor!.Changed -= OnDocumentEdited;
        this.editor = null;

        long caretOffset = Caret?.Caret.Offset ?? 0;
        var selection = Caret?.Selection ?? default;

        SwapRows(new RawRowCollection(session.Index, session.Bytes));
        Caret = new RawCaretController(session.Index, session.Bytes);
        RestoreCaret(caretOffset, selection);

        if (this.toolbar is { } toolbar)
            toolbar.CanChangeWrapWidth = true;

        OnPropertyChanged(nameof(RowCount));
    }

    private void OnDocumentEdited(object? sender, EventArgs e)
    {
        this.rows?.Invalidate();
        EditGeneration++;

        OnPropertyChanged(nameof(EditGeneration));
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(EditStatusText));

        // The caret often sits at the same offset after an edit (forward delete does not move
        // it), so its own notification cannot be relied on to refresh what is under it.
        RefreshCaretReadout();

        StatusText = $"{FilePath} — {RowCount:N0} rows — edited, not saved";
    }

    private void SyncToolbarEditing()
    {
        if (this.toolbar is { } toolbar)
            toolbar.IsEditing = IsEditing;
    }

    /// <summary>Puts the caret and selection back after the row index under them was replaced.
    /// Anchor first, then extend, so a selection made right-to-left keeps its direction.</summary>
    private void RestoreCaret(long caretOffset, RawSelection selection)
    {
        if (Caret is not { } caret)
            return;

        if (selection.IsEmpty)
        {
            caret.PlaceAt(caretOffset);
        }
        else
        {
            caret.PlaceAt(selection.Anchor);
            caret.ExtendTo(selection.Active);
        }
    }

    private void SwapRows(RawRowCollection replacement)
    {
        var old = this.rows;
        this.rows = replacement;
        old?.Dispose();
        OnPropertyChanged(nameof(Rows));
    }

    private void OnCaretMoved(object? sender, EventArgs e) => RefreshCaretReadout();

    /// <summary>
    /// Re-reads the document around the caret. Called on every caret move, which is why
    /// <see cref="RawCaretReadout.Describe"/> is bounded rather than exact.
    /// </summary>
    private void RefreshCaretReadout()
    {
        CaretReadout = this.caret is null || RowIndex is not { } rowIndex || Document is not { } document
            ? null
            : RawCaretReadout.Describe(rowIndex, document, this.caret.Caret, this.caret.Selection);

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

    public async Task LoadAsync(IByteOrigin origin, IProgressReporter? progressReporter = null)
    {
        this.Origin = origin;
        this.FilePath = origin.Path ?? origin.DisplayName;
        this.wrapWidth = RawWrapWidthPreference.Load();
        this.toolbar = new RawToolbarViewModel(this.wrapWidth, SetWrapWidth, SetEditing);

        var session = StartSession(origin, progressReporter);

        // Await a small initial batch so the first paint isn't an empty list; RowCount then
        // tracks the published row count live as indexing continues in the background.
        await session.Index.WaitForRowCountAsync(InitialIndexedRowTarget);

        if (session.Index.Failure is { } failure)
            IndexFailure = failure;

        this.rows = new RawRowCollection(session.Index, session.Bytes);
        Caret = new RawCaretController(session.Index, session.Bytes);

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{FilePath} — {RowCount:N0} rows indexed so far";
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

        // A piece table's rows are derived from the file scan at the wrap width that scan used,
        // so re-wrapping an edited document means re-scanning the edited bytes - the same
        // background re-index a dirty-span rebuild needs, and not built. The toolbar disables
        // the combo for the same reason; this is the guard behind it.
        if (this.editor is not null || IsSaving)
        {
            ToastService.Show("Wrap width cannot change while the document is being edited.");
            return;
        }

        // Raised BEFORE the Rows swap below - the view reacts by resetting its scroll and
        // re-laying-out against the old collection, so the virtualizer's remembered viewport
        // is back at the top when the new ItemsSource arrives (see RawView's
        // ResetScrollBeforeSourceSwap). Reordering this method breaks that contract.
        WrapWidth = bytes;
        IndexGeneration++;
        OnPropertyChanged(nameof(IndexGeneration));

        // Clear selection before the swap - stale indexes must never be applied to the new list.
        SelectedRowIndex = null;

        // Stale relative to the new scan about to start; MonitorIndexing repopulates it if the
        // new scan fails.
        IndexFailure = null;

        this.session.RestartIndex(bytes, StartReindexProgress(FilePath));

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

    /// <summary>Retires any re-index progress still reporting and starts a fresh one, naming the
    /// document as <paramref name="subject"/>.</summary>
    private StatusLineProgress StartReindexProgress(string subject)
    {
        this.reindexProgress?.Stop();
        var progress = new StatusLineProgress(subject, () => !IsDisposed, text => StatusText = text);
        this.reindexProgress = progress;
        return progress;
    }

    /// <summary>Opens <paramref name="origin"/> and starts scanning it, reading through a
    /// <see cref="RemappableByteSource"/> so a later save can unmap it.</summary>
    private RawIndexSession StartSession(IByteOrigin origin, IProgressReporter? progressReporter)
    {
        var bytes = new RemappableByteSource(origin.Open());
        var session = RawIndexSession.Start(bytes, this.wrapWidth, progressReporter);
        this.fileBytes = bytes;
        this.session = session;
        return session;
    }

    // ---- saving -------------------------------------------------------------------------
    //
    // docs/save-plan.md. The order is fixed by Windows, which cannot replace a file while any
    // mapping of it is open - yet the copy reads the original through exactly that mapping:
    //
    //   stage -> copy the document into it and flush (background) -> unmap -> commit -> reopen
    //
    // A failed commit leaves the file untouched, so the mapping is put back and the edits carry
    // on over it as if nothing happened. The caller has already stopped and joined search, the
    // only other reader of this file.

    /// <inheritdoc />
    public async Task<DocumentSaveResult> SaveAsync(IByteOrigin destination, IFileReplacer replacer, IProgressReporter? progress)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(replacer);

        if (!CanSave || Document is not { } document || this.fileBytes is not { } fileBytes)
            return DocumentSaveResult.NotSaved("The document can be saved once it has finished loading.");

        string statusBefore = StatusText;
        long length = document.AvailableLength;
        IsSaving = true;

        StagedFile? stagedOrNull = null;
        try
        {
            stagedOrNull = replacer.Stage(destination, length);

            var cts = new CancellationTokenSource();
            this.saveCts = cts;
            var stage = stagedOrNull;
            this.saveCopy = Task.Run(() =>
            {
                document.WriteTo(stage.Content, progress, cts.Token);
                stage.Seal();
            });

            await this.saveCopy;
        }
        catch (Exception ex)
        {
            stagedOrNull?.Dispose();
            IsSaving = false;
            if (!IsDisposed)
                StatusText = statusBefore;

            return ex is OperationCanceledException
                ? DocumentSaveResult.NotSaved("The save was stopped.")
                : DocumentSaveResult.NotSaved($"Couldn't save: {ex.Message}");
        }
        finally
        {
            this.saveCts = null;
        }

        var staged = stagedOrNull;

        // Closed while the copy ran: disposal joined the copy, and the session is gone.
        if (IsDisposed)
        {
            staged.Dispose();
            return DocumentSaveResult.NotSaved("The document was closed before the save finished.");
        }

        try
        {
            var result = CommitAndReopen(staged, destination, fileBytes);
            if (result.Outcome != DocumentSaveOutcome.Saved)
                StatusText = statusBefore;

            return result;
        }
        finally
        {
            staged.Dispose();
            IsSaving = false;
        }
    }

    /// <summary>
    /// The synchronous middle of a save, on the UI thread so nothing draws or reads the document
    /// while its mapping is gone: unmap, swap, and either reopen over the result or put the old
    /// mapping back.
    /// </summary>
    private DocumentSaveResult CommitAndReopen(StagedFile staged, IByteOrigin destination, RemappableByteSource fileBytes)
    {
        var origin = Origin!;
        long mappedLength = fileBytes.AvailableLength;

        fileBytes.Unmap();
        try
        {
            staged.Commit();
        }
        catch (Exception commitFailure)
        {
            try
            {
                fileBytes.Remap(origin.Open(), mappedLength);
            }
            catch (Exception reopenFailure)
            {
                // The swap may have failed half way (Windows can remove the destination and then
                // fail to rename the replacement into place), so the stage may be the only copy
                // of anything - the edits, and possibly the file itself. Never delete it here.
                staged.KeepStagedContent();
                return new DocumentSaveResult(DocumentSaveOutcome.NotSavedAndDocumentLost,
                    $"Couldn't save: {commitFailure.Message} The file couldn't be reopened either ({reopenFailure.Message}). " +
                    $"What was being saved is in {staged.Location}.");
            }

            return DocumentSaveResult.NotSaved($"Couldn't save: {commitFailure.Message} Your edits are still open.");
        }

        // Before reopening: a small file can finish its fresh scan synchronously inside
        // ReopenOver, and resuming edit mode from there must not find a save still running.
        IsSaving = false;
        ReopenOver(destination);
        return DocumentSaveResult.Saved;
    }

    /// <summary>
    /// Replaces everything this document reads with a fresh scan of <paramref name="destination"/>,
    /// which now holds exactly what was on screen - so the edits, the undo history and the piece
    /// table are all retired, and the caret goes back to the same byte offset, which means the same
    /// place in the saved file as it did in the edited document.
    ///
    /// Everything is swapped before anything is announced: the outgoing session reads a mapping
    /// that is already gone, and a property notification is what would make the view read it.
    /// </summary>
    private void ReopenOver(IByteOrigin destination)
    {
        long caretOffset = Caret?.Caret.Offset ?? 0;
        bool wasEditing = IsEditing;

        if (this.editor is { } editor)
        {
            editor.Changed -= OnDocumentEdited;
            this.editor = null;
        }

        var retiredRows = this.rows;
        var retiredSession = this.session;

        // Named for where it now reads from - after a Save As, the new file.
        var session = StartSession(destination, StartReindexProgress(destination.Path ?? destination.DisplayName));
        this.rows = new RawRowCollection(session.Index, session.Bytes);
        retiredRows?.Dispose();
        retiredSession?.Dispose();

        this.isEditing = false;
        Origin = destination;
        IndexGeneration++;

        FilePath = destination.Path ?? destination.DisplayName;
        SelectedRowIndex = null;
        IndexFailure = null;
        Caret = new RawCaretController(session.Index, session.Bytes);

        if (this.toolbar is { } toolbar)
        {
            toolbar.CanEdit = false;
            toolbar.CanChangeWrapWidth = true;
            toolbar.IsEditing = false;
        }

        OnPropertyChanged(nameof(IndexGeneration));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(EditStatusText));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));

        StatusText = $"{FilePath} — saved — {RowCount:N0} rows indexed so far";
        MonitorIndexing();

        _ = RestoreAfterReopenAsync(caretOffset, wasEditing);
    }

    /// <summary>
    /// Puts the caret back where it was, then - if the save happened in edit mode - turns edit
    /// mode back on once the fresh scan allows it. One sequence rather than two independent
    /// continuations, so the caret is always placed before edit mode takes its position over.
    /// </summary>
    private async Task RestoreAfterReopenAsync(long caretOffset, bool resumeEditing)
    {
        await JumpToByteOffsetAsync(caretOffset);
        if (!resumeEditing || IsDisposed)
            return;

        var indexing = IndexingTask;
        try
        {
            await indexing;
        }
        catch
        {
            return; // failed or cancelled: OnIndexingFailed reports it, and there is nothing to edit
        }

        if (!IsDisposed && ReferenceEquals(indexing, IndexingTask) && CanEdit)
            SetEditing(true);
    }

    /// <summary>A save still copying when the document closes is stopped and joined here, before
    /// the session releases the mapping the copy reads.</summary>
    protected override void DisposeCore()
    {
        this.reindexProgress?.Stop();

        this.saveCts?.Cancel();
        try { this.saveCopy.Wait(); } catch { /* observed only to unblock disposal; SaveAsync reports it */ }
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
    {
        // Before the final text, so a last "(100%)" still queued on the dispatcher is dropped.
        this.reindexProgress?.Stop();

        StatusText = $"{FilePath} — {RowCount:N0} rows";

        // Editing waits for the scan, so this is the moment the toggle becomes usable.
        if (this.toolbar is { } toolbar)
            toolbar.CanEdit = true;

        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Indexing stopped early (failure, or cancellation on <paramref name="failure"/> null).</summary>
    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        this.reindexProgress?.Stop();

        IndexFailure = failure;
        StatusText = failure is { } f
            ? $"{FilePath} — indexing stopped — {f.ItemsIndexed:N0} rows shown"
            : $"{FilePath} — indexing failed";
    }
}
