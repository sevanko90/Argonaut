using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Text;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Binary;

/// <summary>
/// A binary document - an image, an archive, anything that is not text - described rather than
/// drawn: what it is (from its signature, else its media type), how large, and the two things
/// worth doing with it, saving a copy and opening it in another app. Reads only the first bytes,
/// so a binary document of any size opens at once. The view switcher still offers the text views.
/// </summary>
public sealed class BinaryViewModel(IBinaryDocumentActions? actions) : ObservableObject, IDocumentViewModel
{
    private IByteOrigin? origin;
    private string title = string.Empty;
    private string detail = string.Empty;

    public IByteOrigin? Origin => origin;

    public string FilePath => origin?.LocationText() ?? string.Empty;

    /// <summary>The format its signature names ("PNG image"), or "Binary data".</summary>
    public string Title
    {
        get => title;
        private set => SetField(ref title, value);
    }

    /// <summary>The media type its source gave, if any, and its size.</summary>
    public string Detail
    {
        get => detail;
        private set => SetField(ref detail, value);
    }

    /// <summary>Whether saving a copy and opening elsewhere are on offer.</summary>
    public bool OffersActions => actions is not null && origin is not null;

    public string StatusDetail => origin is null ? string.Empty : $"Binary · {ByteLengthText.Format(origin.AvailableLength)}";

    public object? Toolbar => null;

    public IndexFailure? IndexFailure => null;

    public Task IndexingTask => Task.CompletedTask;

    public Task LoadAsync(IByteOrigin document)
    {
        origin = document;
        string? format;
        var source = document.Open();
        try
        {
            format = BinaryContent.FormatOf(source.GetContiguousSpan(0, (int)Math.Min(source.AvailableLength, BinaryContent.SniffLength)));
        }
        finally
        {
            source.Release();
        }

        Title = format ?? "Binary data";
        var parts = new List<string>(2);
        if (document.MediaType is { Length: > 0 } mediaType)
            parts.Add(mediaType);
        parts.Add(ByteLengthText.Format(document.AvailableLength));
        Detail = string.Join(" · ", parts);
        OnPropertyChanged(nameof(OffersActions));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(FilePath));
        return Task.CompletedTask;
    }

    public Task SaveCopyAsync() => actions is not null && origin is not null ? actions.SaveCopyAsync(origin) : Task.CompletedTask;

    public Task OpenInDefaultAppAsync() => actions is not null && origin is not null ? actions.OpenInDefaultAppAsync(origin) : Task.CompletedTask;

    public ISearchNavigator? CreateSearchNavigator() => null;

    public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => fileType == FileTypeDetector.FileKind.Binary;

    /// <summary>Holds nothing of its own: the shell owns the origin, and the prefix read is
    /// released as soon as it is read.</summary>
    public void Dispose()
    {
    }
}
