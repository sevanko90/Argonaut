using System;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Text;
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
    private IByteOrigin? origin;
    private IByteSource? source;
    private HexToolbarViewModel? toolbar;

    public HexViewModel(IDocumentExportActions? actions = null)
    {
        this.actions = actions;
        Cursor = new HexCursor(() => Length);
        Cursor.Moved += OnCaretMoved;
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

    /// <summary>The caret and selection, moved by the surface and by reveals.</summary>
    public HexCursor Cursor { get; }

    /// <summary>Where the caret is: "Byte 1,234    0x000004D2" - its offset in decimal, as the raw
    /// view gives a caret's, then in hex as the offset gutter shows it.</summary>
    public string CaretPositionText
        => Cursor.Caret is { } caret
            ? $"Byte {caret.Byte:N0}    0x{caret.Byte.ToString("X").PadLeft(HexRows.OffsetDigits(Length), '0')}"
            : string.Empty;

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
    }

    public Task LoadAsync(IByteOrigin document)
    {
        this.origin = document;
        this.source = document.Open();
        var start = this.source.GetContiguousSpan(0, (int)Math.Min(this.source.AvailableLength, BinaryContent.SniffLength));
        this.toolbar = new HexToolbarViewModel(document, BinaryContent.FormatOf(start), this.actions);
        OnPropertyChanged(nameof(Toolbar));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(StatusDetail));
        OnCaretMoved(this, EventArgs.Empty);
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

    public void Dispose()
    {
        var held = this.source;
        this.source = null;
        held?.Release();
    }
}
