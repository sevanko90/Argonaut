using System;
using Argonaut.Engine.Bytes;

namespace Argonaut.Shell.Files;

/// <summary>
/// How the shell describes a file it is about to remember, and gets permission to open one it
/// remembered: by bookmark on macOS, in every build, and by path elsewhere. Chosen in the
/// composition root (<see cref="FileAccesses.ForThisPlatform"/>).
/// </summary>
public interface IFileAccess
{
    /// <summary>The locator to remember for <paramref name="path"/> - with its bookmark where the
    /// platform has them. Never fails: without a bookmark it is the path alone.</summary>
    FileLocator Describe(string path);

    /// <summary>
    /// Permission to open <paramref name="locator"/>'s file now, and where it is - which, through a
    /// bookmark, may be a new path with a refreshed bookmark. A bookmark that will not resolve falls
    /// back to the saved path; whether a file is actually there is for the caller to find out.
    /// The caller owns the grant (a <see cref="FileByteOrigin"/> opened with it takes it over).
    /// </summary>
    FileAccessGrant Open(FileLocator locator);
}

/// <summary>Files by path, with nothing to give back - Windows and Linux.</summary>
public sealed class PlainFileAccess : IFileAccess
{
    public FileLocator Describe(string path) => new(path);

    public FileAccessGrant Open(FileLocator locator) => new(locator);
}

/// <summary>Chooses this platform's <see cref="IFileAccess"/>.</summary>
public static class FileAccesses
{
    public static IFileAccess ForThisPlatform()
        => OperatingSystem.IsMacOS() ? new MacFileAccess() : new PlainFileAccess();
}
