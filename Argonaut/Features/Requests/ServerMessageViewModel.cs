using System;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Ui.Documents;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Requests;

/// <summary>
/// What a failed request's server said, shown through the app's own views rather than as plain
/// text: detected as a file would be, then hosted - JSON and XML in their tree with Raw beside
/// them, anything else (HTML included) in the raw view alone. The switch swaps only this view,
/// never the window's document.
/// </summary>
public sealed class ServerMessageViewModel : ObservableObject, IDisposable
{
    private const string MessageName = "Server message";

    private readonly byte[] bytes;
    private bool showsRaw;

    /// <param name="load">Loads a hosted view of a kind over an origin - from the shell, which
    /// sets them up for this view (fully expanded, no schema).</param>
    public ServerMessageViewModel(byte[] bytes, Func<FileTypeDetector.FileKind, IByteOrigin, Task<IDocumentViewModel>> load)
    {
        this.bytes = bytes;
        DetectedKind = Detect(bytes);
        Shown = new HostedDocument(load);
    }

    /// <summary>JSON or XML when the message is one, null for anything shown only as text.</summary>
    public FileTypeDetector.FileKind? DetectedKind { get; }

    /// <summary>Whether there is a structured view to switch to beside Raw.</summary>
    public bool OffersViews => DetectedKind is not null;

    /// <summary>The structured view's button label.</summary>
    public string DetectedName => DetectedKind == FileTypeDetector.FileKind.Xml ? "XML" : "JSON";

    /// <summary>The view being shown.</summary>
    public HostedDocument Shown { get; }

    /// <summary>Raw rather than the structured view; switching loads the other one.</summary>
    public bool ShowsRaw
    {
        get => showsRaw;
        set
        {
            if (!SetField(ref showsRaw, value))
                return;

            OnPropertyChanged(nameof(ShowsStructured));
            _ = ShowAsync();
        }
    }

    /// <summary>The opposite of <see cref="ShowsRaw"/>, for the other toggle button.</summary>
    public bool ShowsStructured
    {
        get => !showsRaw;
        set => ShowsRaw = !value;
    }

    /// <summary>Shows the message in the chosen view.</summary>
    public Task ShowAsync()
    {
        var kind = showsRaw || DetectedKind is not { } detected ? FileTypeDetector.FileKind.Unidentified : detected;
        return Shown.ShowAsync(kind, () => new MemoryByteOrigin(bytes, MessageName));
    }

    public void Dispose() => Shown.Dispose();

    private static FileTypeDetector.FileKind? Detect(byte[] bytes)
    {
        using var origin = new MemoryByteOrigin(bytes, MessageName);
        return FileTypeDetector.DetectFileType(origin) switch
        {
            FileTypeDetector.FileKind.Json => FileTypeDetector.FileKind.Json,
            FileTypeDetector.FileKind.Xml => FileTypeDetector.FileKind.Xml,
            _ => null,
        };
    }
}
