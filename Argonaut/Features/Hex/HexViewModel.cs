using System;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Text;
using Argonaut.Ui.Documents;
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
public sealed class HexViewModel(IDocumentExportActions? actions = null) : ObservableObject, IDocumentViewModel, IHexDocument
{
    private IByteOrigin? origin;
    private IByteSource? source;
    private HexToolbarViewModel? toolbar;

    public IByteOrigin? Origin => this.origin;

    public string FilePath => this.origin?.LocationText() ?? string.Empty;

    public string StatusDetail => this.source is null ? string.Empty : $"Hex · {ByteLengthText.Format(Length)}";

    public object? Toolbar => this.toolbar;

    /// <summary>The dump's columns only line up in a monospaced font, so it always uses one.</summary>
    public bool FollowsContentFont => false;

    public IndexFailure? IndexFailure => null;

    public Task IndexingTask => Task.CompletedTask;

    public long Length => this.source?.AvailableLength ?? 0;

    private long? shownOffset;
    private byte shownValue;

    /// <summary>The byte the status strip describes - the one under the pointer - or null.</summary>
    public long? ShownOffset => this.shownOffset;

    /// <summary>The shown byte's value, e.g. "0x0A · 10 · 0000 1010 · U+000A LINE FEED"; the
    /// character only for ASCII, which is the only one a single byte is.</summary>
    public string ByteValueText
    {
        get
        {
            if (this.shownOffset is null)
                return string.Empty;

            byte value = this.shownValue;
            string bits = Convert.ToString(value, 2).PadLeft(8, '0');
            string text = $"0x{value:X2} · {value} · {bits[..4]} {bits[4..]}";
            return value < 0x80 ? $"{text} · U+{value:X4} {UnicodeNames.NameOf(value) ?? "unnamed"}" : text;
        }
    }

    /// <summary>Where the shown byte is: "Byte 5    0x00000005", as the raw view gives a caret's.</summary>
    public string BytePositionText
        => this.shownOffset is { } offset ? $"Byte {offset:N0}    0x{offset.ToString("X").PadLeft(HexRows.OffsetDigits(Length), '0')}" : string.Empty;

    /// <summary>Describes the byte at <paramref name="offset"/> in the status strip, or nothing.</summary>
    public void ShowByte(long? offset)
    {
        long? shown = null;
        if (offset is { } at && this.source is { } bytes && at >= 0 && at < bytes.AvailableLength)
        {
            Span<byte> one = stackalloc byte[1];
            if (bytes.CopyTo(at, one) == 1)
            {
                this.shownValue = one[0];
                shown = at;
            }
        }

        this.shownOffset = shown;
        OnPropertyChanged(nameof(ShownOffset));
        OnPropertyChanged(nameof(ByteValueText));
        OnPropertyChanged(nameof(BytePositionText));
    }

    public Task LoadAsync(IByteOrigin document)
    {
        this.origin = document;
        this.source = document.Open();
        var start = this.source.GetContiguousSpan(0, (int)Math.Min(this.source.AvailableLength, BinaryContent.SniffLength));
        this.toolbar = new HexToolbarViewModel(document, BinaryContent.FormatOf(start), actions);
        OnPropertyChanged(nameof(Toolbar));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(StatusDetail));
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
