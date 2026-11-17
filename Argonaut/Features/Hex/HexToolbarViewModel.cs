using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Hex;

/// <summary>
/// The hex view's toolbar: what the document is, when its signature or its source says (a PNG
/// image, <c>image/png</c>), and the two things worth doing with bytes the app only shows - saving
/// a copy and opening them in another app.
/// </summary>
public sealed class HexToolbarViewModel : ObservableObject
{
    private readonly IByteOrigin origin;
    private readonly IDocumentExportActions? actions;
    private readonly HexViewModel? document;

    public HexToolbarViewModel(IByteOrigin origin, string? format, IDocumentExportActions? actions, HexViewModel? document = null)
    {
        this.origin = origin;
        this.actions = actions;
        this.document = document;
        Description = Describe(format, origin.MediaType);
        if (document is not null)
        {
            document.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HexViewModel.GroupSize))
                {
                    OnPropertyChanged(nameof(GroupSizeIndex));
                    OnPropertyChanged(nameof(GroupSizeText));
                }
                else if (e.PropertyName == nameof(HexViewModel.ShowsSidePanel))
                {
                    OnPropertyChanged(nameof(ShowsSidePanel));
                }
                else if (e.PropertyName == nameof(HexViewModel.ReadsUtf8))
                {
                    OnPropertyChanged(nameof(TextEncodingIndex));
                    OnPropertyChanged(nameof(TextEncodingText));
                }
            };
        }
    }

    /// <summary>"PNG image · image/png", either part alone, or null when neither is known.</summary>
    public string? Description { get; }

    /// <summary>The grouping as an index into <see cref="HexRows.GroupSizes"/>, for the
    /// selector's radio buttons.</summary>
    public int GroupSizeIndex
    {
        get => Array.IndexOf(HexRows.GroupSizes, this.document?.GroupSize ?? 1);
        set
        {
            if (this.document is not null && value >= 0 && value < HexRows.GroupSizes.Length)
                this.document.GroupSize = HexRows.GroupSizes[value];
        }
    }

    /// <summary>Whether the side panel is shown, for the toolbar's toggle.</summary>
    public bool ShowsSidePanel
    {
        get => this.document?.ShowsSidePanel ?? false;
        set
        {
            if (this.document is not null)
                this.document.ShowsSidePanel = value;
        }
    }

    /// <summary>How the character column reads its bytes, for the selector's radio buttons:
    /// 0 ASCII, 1 UTF-8.</summary>
    public int TextEncodingIndex
    {
        get => this.document?.ReadsUtf8 == true ? 1 : 0;
        set
        {
            if (this.document is not null && value is 0 or 1)
                this.document.ReadsUtf8 = value == 1;
        }
    }

    public string TextEncodingText => this.document?.ReadsUtf8 == true ? "UTF-8" : "ASCII";

    /// <summary>"1 byte", "4 bytes": what the selector's button says.</summary>
    public string GroupSizeText => (this.document?.GroupSize ?? 1) is 1 ? "1 byte" : $"{this.document!.GroupSize} bytes";

    public bool HasDescription => Description is not null;

    /// <summary>Whether saving a copy and opening elsewhere are on offer.</summary>
    public bool OffersActions => actions is not null;

    public Task SaveCopyAsync() => actions?.SaveCopyAsync(origin) ?? Task.CompletedTask;

    public Task OpenInDefaultAppAsync() => actions?.OpenInDefaultAppAsync(origin) ?? Task.CompletedTask;

    private static string? Describe(string? format, string? mediaType)
    {
        var parts = new List<string>(2);
        if (format is { Length: > 0 })
            parts.Add(format);
        if (mediaType is { Length: > 0 })
            parts.Add(mediaType);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
