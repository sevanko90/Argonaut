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
public sealed class HexToolbarViewModel(IByteOrigin origin, string? format, IDocumentExportActions? actions) : ObservableObject
{
    /// <summary>"PNG image · image/png", either part alone, or null when neither is known.</summary>
    public string? Description { get; } = Describe(format, origin.MediaType);

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
