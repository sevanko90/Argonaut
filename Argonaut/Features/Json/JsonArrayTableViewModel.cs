using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Argonaut.Features.Csv;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;
using Avalonia.Threading;

namespace Argonaut.Features.Json;

/// <summary>
/// One JSON array rendered as a CSV-style grid. A real <see cref="IDocumentViewModel"/>, entered
/// explicitly from the JSON tree and published directly by the shell (never via
/// <see cref="DocumentViewCatalog"/>) - exactly as a diff is, and for the same reason: it claims
/// no <see cref="FileTypeDetector.FileKind"/>, so the view switcher offers no selection for it
/// and picking any view there re-indexes the origin file through the normal switch path.
///
/// It owns an independent <see cref="JsonArrayTableSession"/> over the array's own byte range and
/// shares nothing with the JSON document it came from - which is required rather than tidy: the
/// shell disposes the outgoing document before publishing this one, so the origin's index and
/// mapping are already gone by the time this renders its first row.
///
/// Everything about status, failure reporting, the completion monitor and the disposal ordering
/// is <see cref="IndexedDocumentViewModel"/>'s; what is left here is the load, the column
/// discovery, and the toolbar.
/// </summary>
public sealed class JsonArrayTableViewModel : IndexedDocumentViewModel
{
    /// <summary>
    /// Elements awaited before the first paint, and the width sample. A ceiling, not a promise:
    /// the element index publishes in strides, so the sample is whatever has been published by
    /// then. Widths are a saturating heuristic, so 192 sampled elements and 250 give the same
    /// answer on any real data.
    /// </summary>
    private const int InitialElementTarget = 250;

    private JsonArrayTableSession? session;
    private JsonArrayRowCollection? rows;
    private JsonArrayTableToolbarViewModel? toolbar;
    private CsvStructure? structure;
    private JsonArrayColumnMode mode = JsonArrayColumnMode.ByProperty;

    protected override IDocumentSession? Session => this.session;

    protected override IDisposable? MappedRows => this.rows;

    public JsonArrayRowCollection Rows => this.rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    public CsvStructure Structure => this.structure ?? throw new InvalidOperationException("LoadAsync must complete before Structure is accessed.");

    /// <summary>The sticky header's cells, in the same shape the data rows use.</summary>
    public IReadOnlyList<CsvCell> HeaderCells => this.structure?.HeaderCells ?? [];

    public int RowCount => this.rows?.Count ?? 0;

    public override object? Toolbar => this.toolbar;

    /// <summary>
    /// Null for v1: find is disabled while a table is showing. A navigator over token offsets in
    /// a sub-range mapping is separate work, and null is the contract's supported answer - the
    /// shell hides the find bar via IsFindAvailable. It also sidesteps
    /// <see cref="ISearchNavigator.DocumentTearingDown"/>, which deliberately has no default.
    /// </summary>
    public override ISearchNavigator? CreateSearchNavigator() => null;

    /// <summary>Never reached through the view catalog - this document is only ever constructed
    /// directly, like <see cref="Diff.JsonDiffViewModel"/>.</summary>
    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

    /// <summary>
    /// Opens the byte range [<paramref name="arrayOffset"/>, + <paramref name="arrayLength"/>) of
    /// <paramref name="filePath"/> - which must be exactly one array's <c>[</c>…<c>]</c> - as its
    /// own document, and renders its elements as a table. <paramref name="originPath"/> is the
    /// JSONPath the array sits at in the origin document, carried for the banner and for Back.
    ///
    /// Returns once the row collection exists; indexing and the element walk continue in the
    /// background, monitored for status and failure by the base class.
    /// </summary>
    public async Task LoadAsync(string filePath, long arrayOffset, long arrayLength, string originPath, Func<string, Task>? navigateBack = null)
    {
        FilePath = filePath;

        // Progress is reported by this document rather than through the shell's own reporter,
        // the way a diff does it - the entry point publishes directly and only silences the
        // outgoing load's reporter.
        var session = JsonArrayTableSession.Start(filePath, arrayOffset, arrayLength,
            new ProgressToStatus(this, $"Indexing {Path.GetFileName(filePath)}"));
        this.session = session;

        this.toolbar = new JsonArrayTableToolbarViewModel(originPath, filePath,
            setColumnMode: ApplyColumnMode,
            back: () => navigateBack?.Invoke(originPath) ?? Task.CompletedTask);

        // A small initial batch so the first paint isn't an empty grid, and so there is a real
        // sample to width the columns from; a short array completes the wait via MarkComplete.
        await session.Elements.WaitForElementCountAsync(InitialElementTarget);
        if (IsDisposed)
            return;

        if (session.Failure is { } failure)
            IndexFailure = failure;

        this.structure = DiscoverByPropertyStructure();
        this.rows = new JsonArrayRowCollection(session.Elements, session.Inner.Index, session.Inner.File, this.structure, this.mode);

        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Structure));
        OnPropertyChanged(nameof(HeaderCells));
        OnPropertyChanged(nameof(RowCount));

        StatusText = $"{FilePath} — {RowCount:N0} rows indexed so far";
        MonitorIndexing();
    }

    protected override void OnIndexingCompleted()
    {
        OnPropertyChanged(nameof(RowCount));
        StatusText = $"{FilePath} — {RowCount:N0} rows";
    }

    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        IndexFailure = failure;
        StatusText = failure is { } f
            ? $"{FilePath} — indexing stopped — {f.ItemsIndexed:N0} items indexed"
            : $"{FilePath} — indexing failed";
    }

    /// <summary>
    /// Re-shapes the grid for a new picker selection. Builds a new <see cref="CsvStructure"/> and
    /// hands it to the row collection; the array itself is never re-walked, because element
    /// addressing is independent of how the columns are drawn.
    /// </summary>
    private void ApplyColumnMode(JsonArrayColumnModeOption option)
    {
        if (this.session is not { } current || this.rows is null)
            return;

        this.mode = option.Mode;
        this.structure = option.Mode == JsonArrayColumnMode.ByProperty
            ? BuildByPropertyStructure(current)
            : BuildReshapeStructure(current, option.Columns);

        this.rows.SetShape(this.structure, this.mode);

        OnPropertyChanged(nameof(Structure));
        OnPropertyChanged(nameof(HeaderCells));
        OnPropertyChanged(nameof(RowCount));
    }

    private CsvStructure DiscoverByPropertyStructure()
        => BuildByPropertyStructure(this.session!);

    /// <summary>
    /// Discovers the columns from the sampled elements: the union of their direct property
    /// names, in first-seen order, and a per-column width from the longest CHILD VALUE token
    /// seen for it - not the element's own token length, which for a StartObject is 1 (the brace
    /// itself). Seeded with each name's own length so the header always fits.
    ///
    /// An array whose sampled elements are not objects gets a single "value" column, widthed
    /// from the elements' own token lengths. Ragged input needs no further special case: an
    /// element missing a property leaves that cell empty, and one carrying a key the sample never
    /// saw simply has no column to land in.
    ///
    /// Property names are decoded here, once per distinct column plus one per sampled child -
    /// bounded by the initial batch, and nothing is retained but the names themselves. The row
    /// collection never decodes a name at all; it matches raw UTF-8 against these.
    /// </summary>
    private CsvStructure BuildByPropertyStructure(JsonArrayTableSession current)
    {
        var index = current.Inner.Index;
        var file = current.Inner.File;
        int sample = Math.Min(current.Elements.ElementCount, InitialElementTarget);

        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var maxChars = new List<int>();
        bool sawObject = false;
        int valueChars = 0;

        for (int e = 0; e < sample; e++)
        {
            int token = current.Elements.TokenForElement(e);
            var element = index.GetToken(token);

            if (element.Kind != JsonTokenKind.StartObject)
            {
                valueChars = Math.Max(valueChars, element.Length);
                continue;
            }

            sawObject = true;
            for (int child = token + 1; child < element.EndIndex;)
            {
                var info = index.GetToken(child);
                if (info.NameLength >= 0)
                {
                    string name = DisplayText.Read(file, info.NameOffset, info.NameLength, out _);
                    if (!columns.TryGetValue(name, out int column))
                    {
                        column = names.Count;
                        columns[name] = column;
                        names.Add(name);
                        maxChars.Add(name.Length);
                    }

                    maxChars[column] = Math.Max(maxChars[column], info.Length);
                }

                child = IsContainer(info.Kind) ? info.EndIndex + 1 : child + 1;
            }
        }

        if (!sawObject)
        {
            names.Add("value");
            maxChars.Add(Math.Max(valueChars, "value".Length));
        }

        return CsvStructure.FromMaxChars(names, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(maxChars));
    }

    /// <summary>
    /// Generic "Column N" labels, widthed from the sampled elements' OWN token lengths bucketed
    /// by <c>i % columns</c> - here the element is exactly one cell, so its own length is the
    /// right measure. No file read and no decoded text: <see cref="JsonTokenInfo.Length"/> is
    /// unpacked O(1) from the token log, which is why re-widthing for a new N costs a walk over
    /// the sample and needs no cache. (An array of objects reshaped this way measures 1 per
    /// element - the brace - and lands on the minimum width, which is the honest result for
    /// cells that show a container summary.)
    /// </summary>
    private CsvStructure BuildReshapeStructure(JsonArrayTableSession current, int columns)
    {
        var index = current.Inner.Index;
        int sample = Math.Min(current.Elements.ElementCount, InitialElementTarget);

        var names = new string[columns];
        var maxChars = new int[columns];
        for (int c = 0; c < columns; c++)
        {
            names[c] = $"Column {c + 1}";
            maxChars[c] = names[c].Length;
        }

        for (int e = 0; e < sample; e++)
        {
            int token = current.Elements.TokenForElement(e);
            int column = e % columns;
            maxChars[column] = Math.Max(maxChars[column], index.GetToken(token).Length);
        }

        return CsvStructure.FromMaxChars(names, maxChars);
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    /// <summary>Marshals background progress reports onto the status line - the same shape as
    /// the shell's StatusProgressReporter and JsonDiffViewModel's: Post (never blocking), ~5%
    /// buckets, silent once the view model is disposed.</summary>
    private sealed class ProgressToStatus : IProgressReporter
    {
        private readonly JsonArrayTableViewModel owner;
        private readonly string label;
        private int lastBucket = -1;

        public ProgressToStatus(JsonArrayTableViewModel owner, string label)
        {
            this.owner = owner;
            this.label = label;
        }

        public void Report(string message, long? current = null, long? max = null)
        {
            string text = this.label;
            if (current.HasValue && max.HasValue && max.Value > 0)
            {
                int percent = (int)Math.Min(100, current.Value * 100L / max.Value);
                int bucket = percent / 5;
                if (bucket == this.lastBucket)
                    return;

                this.lastBucket = bucket;
                text += $"… ({percent}%)";
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!this.owner.IsDisposed)
                    this.owner.StatusText = text;
            });
        }
    }
}
