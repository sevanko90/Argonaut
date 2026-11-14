using System;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Requests;

/// <summary>
/// The POST body box: the raw editor hosted over the body's bytes - a nested
/// <see cref="RawViewModel"/> on an editable in-memory origin, the way New document runs one - so
/// a body gets the editor's colouring, undo and line endings rather than a second, lesser editing
/// behaviour. Owns the hosted view model and disposes it.
/// </summary>
public sealed class RequestBodyEditor(RawViewSettings settings) : ObservableObject, IDisposable
{
    private RawViewModel? editor;

    // Which load is the latest: one overtaken by another, or by Dispose, discards what it loaded.
    private int loads;
    private bool disposed;

    private const string BodyName = "Body";

    /// <summary>The hosted editor the view shows; null until <see cref="LoadAsync"/>.</summary>
    public RawViewModel? Editor
    {
        get => editor;
        private set
        {
            if (SetField(ref editor, value))
                EditorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="Editor"/> is replaced.</summary>
    public event EventHandler? EditorChanged;

    /// <summary>
    /// Starts editing <paramref name="body"/>, replacing whatever was being edited. The new editor
    /// is published only once it has loaded - a view bound to it lays out its rows at once, and a
    /// raw view model has none until its load completes - the same rule the shell keeps for every
    /// document it shows.
    /// </summary>
    public async Task LoadAsync(byte[] body)
    {
        int load = ++loads;
        var next = new RawViewModel(settings);
        await next.LoadAsync(new MemoryByteOrigin(body, BodyName, startsEditable: true));

        if (disposed || load != loads)
        {
            next.Dispose();
            return;
        }

        var previous = Editor;
        Editor = next;
        previous?.Dispose();
    }

    /// <summary>The body as it stands, edits included; empty before anything has loaded.</summary>
    public byte[] ReadBody()
    {
        if (Editor?.Document is not { } document)
            return [];

        var bytes = new byte[document.AvailableLength];
        int copied = 0;
        while (copied < bytes.Length)
        {
            int read = document.CopyTo(copied, bytes.AsSpan(copied));
            if (read == 0)
                break;
            copied += read;
        }

        return copied == bytes.Length ? bytes : bytes[..copied];
    }

    public void Dispose()
    {
        disposed = true;
        Editor?.Dispose();
        Editor = null;
    }
}
