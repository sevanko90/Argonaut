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
/// Owns its file and deletes it when disposed. A mapping still open then (a search scanning a
/// chunk of it) keeps the bytes readable on macOS and Linux; on Windows the delete fails and the
/// file is left for the response folder's sweep.
/// </summary>
public sealed class HttpResponseOrigin : IByteOrigin
{
    private readonly string path;

    public HttpResponseOrigin(HttpLocator locator, string path)
    {
        ArgumentNullException.ThrowIfNull(locator);
        Locator = locator;
        this.path = path;
        DisplayName = locator.DefaultName;
    }

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
