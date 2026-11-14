using System;
using System.Threading;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// Permission to read one file for as long as this is held: the file as found - which a bookmark
/// may have resolved somewhere other than where it was saved - and what gives the permission back. On macOS that is the security-scoped
/// access a sandboxed build needs; elsewhere there is nothing to give back.
///
/// The <see cref="FileByteOrigin"/> opened with it holds it and releases it on dispose, so access
/// lives exactly as long as the input does.
/// </summary>
public sealed class FileAccessGrant(FileLocator locator, Action? release = null) : IDisposable
{
    private Action? release = release;

    /// <summary>The file as found: a moved file's new path, with a refreshed bookmark.</summary>
    public FileLocator Locator { get; } = locator;

    /// <summary>Where to open the file now.</summary>
    public string Path => Locator.Path;

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
