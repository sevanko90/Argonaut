using System;
using System.IO;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Saving;

/// <summary>
/// Hands a document's bytes to the world outside the app unchanged: a copy where the user chose,
/// or a file another app can open. Both read the document through its own origin, so a paste, a
/// download and a file are copied the same way, and both run on the background - a document can
/// be gigabytes.
/// </summary>
public interface IOriginExporter
{
    /// <summary>Writes the document's bytes to <paramref name="destination"/>, replacing a file
    /// there (the save dialog has asked). Writing a file over itself does nothing.</summary>
    /// <exception cref="IOException">The copy failed; nothing partial is left behind.</exception>
    /// <exception cref="UnauthorizedAccessException">The destination cannot be written.</exception>
    Task CopyAsync(IByteOrigin origin, string destination);

    /// <summary>
    /// A file holding the document that another app can open, named <paramref name="fileName"/> so
    /// the system picks that app by its extension: the document's own file when it is one,
    /// otherwise a copy in the exporter's folder, which <see cref="Sweep"/> clears at the next
    /// start.
    /// </summary>
    /// <exception cref="IOException">The copy failed.</exception>
    Task<string> PrepareToOpenAsync(IByteOrigin origin, string fileName);

    /// <summary>Deletes the copies an earlier run made for opening. Best-effort: one another app
    /// still holds (on Windows) is left for the next start.</summary>
    void Sweep();
}

/// <summary><see cref="IOriginExporter"/> over the file system; <paramref name="openFolder"/>, from
/// the composition root, holds the copies made for opening.</summary>
public sealed class OriginExporter(string openFolder) : IOriginExporter
{
    private const int ChunkBytes = 1 << 20;

    public Task CopyAsync(IByteOrigin origin, string destination)
    {
        if (origin.Locator is FileLocator file && SamePath(file.Path, destination))
            return Task.CompletedTask;

        return Task.Run(() => Write(origin, destination));
    }

    public async Task<string> PrepareToOpenAsync(IByteOrigin origin, string fileName)
    {
        if (origin.Locator is FileLocator file && File.Exists(file.Path))
            return file.Path;

        // A folder per copy, so the file keeps the name the opening app will show.
        string folder = Path.Combine(openFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName);
        await Task.Run(() => Write(origin, path));
        return path;
    }

    public void Sweep()
    {
        if (!Directory.Exists(openFolder))
            return;

        foreach (string folder in Directory.EnumerateDirectories(openFolder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in another app; the next start tries again.
            }
        }
    }

    /// <summary>Copies chunk by chunk, advancing by what each read returned - a source may hand
    /// back less than asked at an internal boundary.</summary>
    private static void Write(IByteOrigin origin, string destination)
    {
        var source = origin.Open();
        bool opened = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                opened = true;
                long offset = 0;
                while (true)
                {
                    var chunk = source.GetContiguousSpan(offset, ChunkBytes);
                    if (chunk.IsEmpty)
                        break;

                    output.Write(chunk);
                    offset += chunk.Length;
                }
            }
        }
        catch
        {
            // Only what this copy started writing: a destination that could not be opened is
            // still the user's file as it was.
            if (opened)
                TryDelete(destination);
            throw;
        }
        finally
        {
            source.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; the failure being reported is the copy's.
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
