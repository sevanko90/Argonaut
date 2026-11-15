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

    public IndexFailure? IndexFailure => null;

    public Task IndexingTask => Task.CompletedTask;

    public long Length => this.source?.AvailableLength ?? 0;

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
