using System;
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
public sealed class RawViewModel : ObservableObject, IDocumentViewModel
{
    private const int InitialIndexedRowTarget = 250;

    private RawIndexSession? session;
    private RawRowCollection? rows;
    private RawToolbarViewModel? toolbar;
    private string? highlightTerm;
    private string statusText = string.Empty;
    private int? selectedRowIndex;
    private int wrapWidth = RawWrapWidthPreference.Default;
    private bool disposed;

    public string FilePath { get; private set; } = string.Empty;

    internal RawSegmentIndex? Index => this.session?.Index;

    internal MMapFile? Mmap => this.session?.File;

    public Task IndexingTask => this.session?.IndexingTask ?? Task.CompletedTask;

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

    /// <summary>Status-bar line for this document (see <see cref="IDocumentViewModel"/>).</summary>
    public string StatusText
    {
        get => this.statusText;
        private set => SetField(ref this.statusText, value);
    }

    public RawRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    public object? Toolbar => this.toolbar;

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

        this.rows = new RawRowCollection(session.Index, session.File);

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{path} — {RowCount:N0} rows indexed so far";
        _ = MonitorIndexingAsync(session.Index);
    }

    /// <summary>
    /// Applies a new wrap width by re-indexing the same mapping. Synchronous and entirely
    /// internal: a running search is unaffected (matches are byte offsets over the unchanged
    /// file), and the growth timer of the fresh collection fills rows in within ~120ms.
    /// </summary>
    public void SetWrapWidth(int bytes)
    {
        if (this.disposed || this.session is null || bytes == this.wrapWidth)
            return;

        // Raised BEFORE the Rows swap below - the view reacts by resetting its scroll and
        // re-laying-out against the old collection, so the virtualizer's remembered viewport
        // is back at the top when the new ItemsSource arrives (see RawView's
        // ResetScrollBeforeSourceSwap). Reordering this method breaks that contract.
        WrapWidth = bytes;
        IndexGeneration++;

        // Clear selection before the swap - stale indexes must never be applied to the new list.
        SelectedRowIndex = null;

        this.session.RestartIndex(bytes);

        var old = this.rows;
        this.rows = new RawRowCollection(this.session.Index, this.session.File);
        old?.Dispose();

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{FilePath} — {RowCount:N0} rows indexed so far";
        _ = MonitorIndexingAsync(this.session.Index);
    }

    public ISearchNavigator CreateSearchNavigator() => new RawSearchNavigator(this);

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    public bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Unidentified;
    }

    /// <summary>
    /// Refreshes <see cref="StatusText"/> when background indexing finishes or fails. The
    /// generation guard (index still current) covers both dispose-cancellation and a wrap
    /// change retiring this index mid-monitor - a retired scan's cancellation fault must not
    /// repaint the status as a failure.
    /// </summary>
    private async Task MonitorIndexingAsync(RawSegmentIndex index)
    {
        try
        {
            await index.IndexingTask;
        }
        catch
        {
            if (!this.disposed && ReferenceEquals(index, this.session?.Index))
                StatusText = $"{FilePath} — indexing failed";
            return;
        }

        if (!this.disposed && ReferenceEquals(index, this.session?.Index))
            StatusText = $"{FilePath} — {RowCount:N0} rows";
    }

    public void Dispose()
    {
        // Idempotent - see IDocumentViewModel's lifetime contract.
        if (this.disposed)
            return;
        this.disposed = true;

        // Cancel first so the background scan stops promptly; the row collection must be
        // disposed before session.Dispose joins the scans and releases the mapping.
        this.session?.Cancel();
        this.rows?.Dispose();
        this.session?.Dispose();
    }
}
