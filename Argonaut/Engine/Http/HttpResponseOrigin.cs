using System;
using System.IO;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Http;

/// <summary>
/// A downloaded response: a complete file in the response folder, read the way a file is - mapped,
/// settled from birth - but reporting the request it answered as its locator. So the shell keeps
/// the request as the saved source, a save is Save As as for a paste, and the response file is
/// never mistaken for a document of the user's.
///
/// Owns its file and deletes it when disposed - unless the response was kept for its saved
/// request (<see cref="MarkKept"/>), when the file outlives the document. A mapping still open at
/// the delete (a search scanning a chunk of it) keeps the bytes readable on macOS and Linux; on
/// Windows the delete fails and the file is left for the response folder's sweep.
/// </summary>
public sealed class HttpResponseOrigin : IByteOrigin
{
    private readonly string path;
    private bool kept;

    /// <param name="mediaType">What the server said the response is (<c>Content-Type</c> without
    /// parameters), or null when it said nothing.</param>
    /// <param name="kept">The file is a kept response being reopened, not a fresh download.</param>
    public HttpResponseOrigin(HttpLocator locator, string path, string? mediaType, bool kept = false)
    {
        ArgumentNullException.ThrowIfNull(locator);
        Locator = locator;
        this.path = path;
        this.kept = kept;
        MediaType = mediaType;
        DisplayName = locator.DefaultName;
    }

    /// <summary>What the server said the response is, or null - what a saved copy's extension
    /// comes from (<see cref="ResponseFileNames"/>).</summary>
    public string? MediaType { get; }

    /// <summary>The file's name in the response folder - what a saved request records to keep it.</summary>
    public string ResponseFileName => Path.GetFileName(this.path);

    /// <summary>Whether the file is kept for a saved request, and so outlives this origin.</summary>
    public bool IsKept => kept;

    /// <summary>Keeps the file when this origin closes: it is now a saved request's response.</summary>
    public void MarkKept() => kept = true;

    /// <summary>The file is no longer kept - its request let it go while it is open - so this
    /// origin deletes it when it closes, as it would a fresh download.</summary>
    public void ReleaseKept() => kept = false;

    public string DisplayName { get; }

    public HttpLocator Locator { get; }

    SourceLocator? IByteOrigin.Locator => Locator;

    /// <summary>Where the response is on disk - for the response folder, never shown or saved to.</summary>
    public string ResponsePath => this.path;

    public long AvailableLength => new FileInfo(this.path).Length;

    public IByteSource Open() => new MMapFile(this.path);

    public IByteSource OpenRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new MMapFile(this.path, offset, length);
    }

    public KeptIndexes KeptIndexes { get; } = new();

    public void Dispose()
    {
        KeptIndexes.Clear();
        if (kept)
            return;

        try
        {
            File.Delete(this.path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the response folder's sweep - see the class remarks.
        }
    }
}
