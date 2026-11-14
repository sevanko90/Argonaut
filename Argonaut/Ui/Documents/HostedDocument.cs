using System;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Ui.Documents;

/// <summary>
/// A document shown inside another view rather than as the window's document - a failed
/// request's server message in its JSON tree, say. Owns what it shows: each show opens its own
/// origin and loads a view model over it through the injected loader, and both are released when
/// replaced or when this is disposed.
///
/// A view model is published (<see cref="Document"/>) only once it has loaded - a view bound to
/// it lays out its rows at once, and a document has none until its load completes - and a load
/// overtaken by another, or by disposal, is thrown away rather than shown. UI thread only.
/// </summary>
/// <param name="load">Loads a view model of the given kind over an origin - the shell decides
/// how a hosted document of each kind is set up.</param>
public sealed class HostedDocument(Func<FileTypeDetector.FileKind, IByteOrigin, Task<IDocumentViewModel>> load) : ObservableObject, IDisposable
{
    private IDocumentViewModel? document;
    private IByteOrigin? origin;
    private int loads;
    private bool disposed;

    /// <summary>What to show, or null until the first load completes.</summary>
    public IDocumentViewModel? Document
    {
        get => document;
        private set => SetField(ref document, value);
    }

    /// <summary>The latest show, for a caller that wants to wait for it.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Shows a <paramref name="kind"/> document over a fresh origin from
    /// <paramref name="makeOrigin"/>, replacing what is shown once it has loaded.</summary>
    public Task ShowAsync(FileTypeDetector.FileKind kind, Func<IByteOrigin> makeOrigin)
        => Loading = ShowCoreAsync(kind, makeOrigin);

    private async Task ShowCoreAsync(FileTypeDetector.FileKind kind, Func<IByteOrigin> makeOrigin)
    {
        int show = ++loads;
        var nextOrigin = makeOrigin();
        IDocumentViewModel next;
        try
        {
            next = await load(kind, nextOrigin);
        }
        catch
        {
            nextOrigin.Dispose();
            throw;
        }

        if (disposed || show != loads)
        {
            next.Dispose();
            nextOrigin.Dispose();
            return;
        }

        var (previous, previousOrigin) = (document, origin);
        origin = nextOrigin;
        Document = next;
        previous?.Dispose();
        previousOrigin?.Dispose();
    }

    public void Dispose()
    {
        disposed = true;
        var (shown, shownOrigin) = (document, origin);
        origin = null;
        Document = null;
        shown?.Dispose();
        shownOrigin?.Dispose();
    }
}
