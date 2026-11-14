using System;
using System.IO;

namespace Argonaut.Engine.Settings;

/// <summary>
/// A sealing key in a file only its owner can read - the Linux store, where no secret service can
/// be assumed. The path comes from the composition root (the config folder), so the key survives a
/// reinstall the way the settings do.
/// </summary>
public sealed class FileSealingKeyStore(string path) : ISealingKeyStore
{
    public byte[]? LoadKey()
    {
        try
        {
            if (!File.Exists(path))
                return null;

            byte[] key = File.ReadAllBytes(path);
            return key.Length == 32 ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool SaveKey(byte[] key)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            // Created with owner-only access rather than tightened after, so the key is never
            // readable by others even for a moment.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(path, options))
                stream.Write(key);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
