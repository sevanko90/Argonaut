using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Text;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Find;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Hex;

/// <summary>
/// Any document as a hex dump (<see cref="HexSurface"/>). There is nothing to index - every row is
/// arithmetic on the offset - so the document is ready the moment its source is open, however
/// large, and <see cref="IndexingTask"/> is complete from the start.
///
/// Owns the source it reads and releases it when disposed; from then on rows read as empty, so a
/// frame drawn after teardown reads nothing rather than a released mapping.
///
/// It is also where a binary document opens: the toolbar names its format from its first bytes and
/// offers to save a copy or open it in another app (<see cref="IDocumentExportActions"/>).
/// </summary>
public sealed class HexViewModel : ObservableObject, IDocumentViewModel, IHexDocument, IByteRangeNavigable
{
    private readonly IDocumentExportActions? actions;
    private readonly HexViewSettings settings;
    private IByteOrigin? origin;
    private IByteSource? source;
    private HexToolbarViewModel? toolbar;

    // A streamed document grows after it opens: a background wait for its length to settle, and
    // a monitor that refreshes what the view shows as the bytes arrive.
    private static readonly TimeSpan GrowthInterval = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? settling;
    private Task? settled;
    private IndexGrowthMonitor? growthMonitor;
    private long shownLength;

    public HexViewModel(IDocumentExportActions? actions = null, HexViewSettings? settings = null)
    {
        this.actions = actions;
        this.settings = settings ?? new HexViewSettings();
        Cursor = new HexCursor(() => Length);
        Cursor.Moved += OnCaretMoved;
        Values = new HexValuesViewModel(
            this.settings.ValuesByteOrder,
            order => this.settings.ValuesByteOrder = order,
            (start, length) => Cursor.Select(new HexCaret(start, start + length)));
    }

    public IByteOrigin? Origin => this.origin;

    public string FilePath => this.origin?.LocationText() ?? string.Empty;

    public string StatusDetail => this.source is null ? string.Empty : $"Hex · {ByteLengthText.Format(Length)}";

    public object? Toolbar => this.toolbar;

    /// <summary>The dump's columns only line up in a monospaced font, so it always uses one.</summary>
    public bool FollowsContentFont => false;

    public IndexFailure? IndexFailure => null;

    public Task IndexingTask => Task.CompletedTask;

    public long Length => this.source?.AvailableLength ?? 0;

    /// <summary>How many bytes the hex panel draws together - display only, remembered for every
    /// hex view.</summary>
    public int GroupSize
    {
        get => this.settings.GroupSize;
        set
        {
            if (value == this.settings.GroupSize)
                return;

            this.settings.GroupSize = value;
            OnPropertyChanged();
            ReadValues();
        }
    }

    /// <summary>Whether the character column reads UTF-8 rather than ASCII - remembered for every
    /// hex view.</summary>
    public bool ReadsUtf8
    {
        get => this.settings.ReadsUtf8;
        set
        {
            if (value == this.settings.ReadsUtf8)
                return;

            this.settings.ReadsUtf8 = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the side panel is shown - remembered for every hex view.</summary>
    public bool ShowsSidePanel
    {
        get => this.settings.ShowsSidePanel;
        set
        {
            if (value == this.settings.ShowsSidePanel)
                return;

            this.settings.ShowsSidePanel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The side panel's Values tab, reading the bytes at the selection start.</summary>
    public HexValuesViewModel Values { get; }

    /// <summary>The caret and selection, moved by the surface and by reveals.</summary>
    public HexCursor Cursor { get; }

    /// <summary>Where the caret is: "Byte 1,234    0x000004D2" - its offset in decimal, as the raw
    /// view gives a caret's, then in hex as the offset gutter shows it.</summary>
    public string CaretPositionText
        => Cursor.Caret is { } caret
            ? $"Byte {caret.Byte:N0}    0x{caret.Byte.ToString("X").PadLeft(HexRows.OffsetDigits(Length), '0')}"
            : string.Empty;

    /// <summary>
    /// The character the caret's byte belongs to, as the raw view names one: "U+00E9 LATIN SMALL
    /// LETTER E WITH ACUTE" - read as UTF-8 whichever way the character column reads, looking up
    /// to three bytes back for the character's start. A byte that is part of no whole character
    /// says so.
    /// </summary>
    public string CaretCharacterText
    {
        get
        {
            if (this.source is not { } from || Cursor.Caret is not { } caret)
                return string.Empty;

            long at = caret.Byte;
            long start = Math.Max(0, at - HexUtf8.Reach);
            Span<byte> window = stackalloc byte[HexUtf8.Reach * 2 + 1];
            int read = from.CopyTo(start, window[..(int)Math.Clamp(from.AvailableLength - start, 0, window.Length)]);
            int index = (int)(at - start);
            if (index >= read)
                return string.Empty;

            if (HexUtf8.CharacterAt(window[..read], index) is not { } character)
                return $"0x{window[index]:X2} not UTF-8";

            return $"U+{character.Value:X4} {UnicodeNames.NameOf(character.Value) ?? "unnamed"}";
        }
    }

    /// <summary>The selection's size, empty when nothing is selected.</summary>
    public string CaretSelectionText
        => Cursor.Caret is { IsEmpty: false } caret ? $"{caret.Length:N0} {(caret.Length == 1 ? "byte" : "bytes")}" : string.Empty;

    public ByteRange? SelectedByteRange => Cursor.Position;

    /// <summary>Selects <paramref name="range"/> with the caret at its start and brings it on
    /// screen. Nothing to wait for: every place in a hex dump is arithmetic.</summary>
    public Task RevealByteRangeAsync(ByteRange range)
    {
        Cursor.Reveal(range);
        return Task.CompletedTask;
    }

    /// <summary>What the go-to entry opens with: the caret's offset in hex, as the gutter shows
    /// it, so going somewhere near is an edit rather than a retype.</summary>
    public string GoToStartText => $"0x{Cursor.Caret?.Byte ?? 0:X}";

    /// <summary>
    /// Puts the caret on the offset <paramref name="entry"/> names (<see cref="HexOffsetEntry"/>)
    /// and brings it on screen. Pure arithmetic, so nothing is waited for.
    /// </summary>
    /// <returns>Null when it went there; otherwise what is wrong with the entry, to show.</returns>
    public string? GoToOffset(string? entry)
    {
        if (!HexOffsetEntry.TryParse(entry, Cursor.Caret?.Byte ?? 0, out long offset))
            return "Not an offset. Type a decimal number, or hex like 0x1F40.";

        long length = Length;
        if (offset >= length)
            return length == 0
                ? "The document is empty."
                : $"0x{offset:X} is past the end. The last byte is 0x{length - 1:X} ({length - 1:N0}).";

        Cursor.Reveal(ByteRange.At(offset));
        return null;
    }

    /// <summary>The selected bytes, for a copy - false when nothing is selected or the selection
    /// is larger than <see cref="HexCopy.MaxBytes"/>.</summary>
    public bool TryReadSelection(out byte[] bytes)
    {
        bytes = [];
        if (this.source is not { } from || Cursor.Caret is not { IsEmpty: false } caret || caret.Length > HexCopy.MaxBytes)
            return false;

        bytes = new byte[caret.Length];
        return from.CopyTo(caret.Start, bytes) == bytes.Length;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CaretPositionText));
        OnPropertyChanged(nameof(CaretSelectionText));
        OnPropertyChanged(nameof(CaretCharacterText));
        ReadValues();
    }

    /// <summary>Reads what the Values tab shows - a selection of one to eight bytes, else the
    /// caret's group (the group a longer selection starts in) - with any bytes past it, up to
    /// eight, for a UTF-8 character to finish; into a stack buffer.</summary>
    private void ReadValues()
    {
        Span<byte> bytes = stackalloc byte[HexValuesViewModel.MaxBytes];
        if (this.source is not { } from || Cursor.Caret is not { } caret)
        {
            Values.Show(0, [], GroupSize, HexRows.MinOffsetDigits);
            return;
        }

        bool selected = caret.Length is > 0 and <= HexValuesViewModel.MaxBytes;
        long start = selected ? caret.Start : HexRows.GroupStart(caret.IsEmpty ? caret.Byte : caret.Start, GroupSize);
        int width = selected ? (int)caret.Length : GroupSize;
        int count = from.CopyTo(start, bytes[..(int)Math.Clamp(from.AvailableLength - start, 0, bytes.Length)]);
        Values.Show(start, bytes[..count], width, HexRows.OffsetDigits(Length));
    }

    public Task LoadAsync(IByteOrigin document)
    {
        this.origin = document;
        this.source = document.Open();
        var start = this.source.GetContiguousSpan(0, (int)Math.Min(this.source.AvailableLength, BinaryContent.SniffLength));
        this.toolbar = new HexToolbarViewModel(document, BinaryContent.FormatOf(start), this.actions, this);
        OnPropertyChanged(nameof(Toolbar));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(StatusDetail));
        OnCaretMoved(this, EventArgs.Empty);
        this.shownLength = this.source.AvailableLength;
        if (!this.source.LengthSettled)
            FollowGrowth(this.source);
        return Task.CompletedTask;
    }

    public int ReadRow(long row, Span<byte> into)
    {
        if (this.source is not { } bytes || row < 0)
            return 0;

        long start = HexRows.StartOf(row);
        long length = bytes.AvailableLength;
        if (start >= length)
            return 0;

        int count = (int)Math.Min(HexRows.BytesPerRow, length - start);
        return bytes.CopyTo(start, into[..count]);
    }

    public ISearchNavigator? CreateSearchNavigator() => null;

    public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => fileType == FileTypeDetector.FileKind.Hex;

    /// <summary>Completes once a streamed document has settled and its last refresh has run - for
    /// tests; null for a document that was whole when it opened.</summary>
    internal Task? GrowthFollowed => this.growthMonitor?.FinalRefreshTask;

    /// <summary>Raised on the UI thread when more of a streamed document has arrived, so the
    /// view can draw the new rows.</summary>
    public event EventHandler? Grew;

    /// <summary>
    /// Watches a streamed document grow: a background wait (<see cref="IByteSource.WaitForLength"/>
    /// blocks by design, so it is never run on the UI thread) completes when the length settles,
    /// and <see cref="IndexGrowthMonitor"/> refreshes on a tick until then and once after.
    /// </summary>
    private void FollowGrowth(IByteSource growing)
    {
        this.settling = new CancellationTokenSource();
        var stopping = this.settling.Token;
        this.settled = Task.Run(() => growing.WaitForLength(long.MaxValue, stopping), stopping);
        this.growthMonitor = new IndexGrowthMonitor(GrowthInterval, this.settled, () => growing.LengthSettled, OnGrowthTick);
    }

    private void OnGrowthTick()
    {
        if (this.source is not { } bytes || bytes.AvailableLength == this.shownLength)
            return;

        this.shownLength = bytes.AvailableLength;
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(Length));
        ReadValues();
        OnPropertyChanged(nameof(CaretCharacterText));
        Grew?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops following growth - cancel, then join the wait - before letting the source
    /// go, so nothing is still reading it once it is released.</summary>
    public void Dispose()
    {
        this.growthMonitor?.Dispose();
        this.growthMonitor = null;
        if (this.settling is { } stop)
        {
            stop.Cancel();
            try
            {
                this.settled?.Wait();
            }
            catch (AggregateException)
            {
                // Cancelled: the wait is over, which is all that is needed.
            }

            stop.Dispose();
            this.settling = null;
        }

        var held = this.source;
        this.source = null;
        held?.Release();
    }
}
