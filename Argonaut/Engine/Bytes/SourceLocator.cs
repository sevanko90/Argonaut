using System;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// Where a document's bytes can be fetched again: the one description of a document's place in
/// the world, which saved sources and remembered schema bindings key on and which reopening
/// reads. A file's path is one kind of locator; a document with nowhere to go back to (a paste,
/// an excerpt, a diff side) has none - see <see cref="IByteOrigin.Locator"/>.
///
/// Anything that touches the file system asks for the file kind (<c>Locator is FileLocator
/// file</c>) and degrades when it is absent, never substituting a display name.
/// </summary>
public abstract record SourceLocator
{
    /// <summary>A name to show for a new saved entry, before the user renames it.</summary>
    public abstract string DefaultName { get; }

    /// <summary>What a person reads as "where this is" - the full path, the URL.</summary>
    public abstract string DisplayText { get; }

    /// <summary>
    /// The normalised form two locators are compared and keyed by: equal identities are the same
    /// place. Never shown. Carries the kind, so a file and a URL can never collide.
    /// </summary>
    public abstract string Identity { get; }

    /// <summary>Whether <paramref name="other"/> names the same place, by <see cref="Identity"/>.</summary>
    public bool IsSamePlace(SourceLocator? other)
        => other is not null && string.Equals(Identity, other.Identity, StringComparison.Ordinal);
}

/// <summary>
/// A file on disk, by its absolute path, and - on macOS - its bookmark: the system's own way back
/// to a file, which follows it when it is moved or renamed and, in a sandboxed build, carries the
/// grant to open it again after a relaunch. The bookmark is opaque here; what makes and reads it
/// is the shell's file access. Identity is the path alone, so a bookmark refreshed on reopening
/// still names the same place.
/// </summary>
public sealed record FileLocator : SourceLocator
{
    /// <param name="path">Made absolute here, so every file locator compares on full paths.</param>
    /// <param name="bookmark">The file's bookmark, or null where the platform has none.</param>
    public FileLocator(string path, byte[]? bookmark = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Bookmark = bookmark;
    }

    public byte[]? Bookmark { get; }

    /// <summary>The absolute path - what the file-system features (saving, revealing, the
    /// line-ending count) open.</summary>
    public string Path { get; }

    public override string DefaultName => System.IO.Path.GetFileName(Path);

    public override string DisplayText => Path;

    /// <summary>The full path, case-folded where the file system usually ignores case (macOS,
    /// Windows). A false match there only ever merges two entries for what is very likely one
    /// file - not worth probing each volume's real case sensitivity.</summary>
    public override string Identity
        => "file:" + (OperatingSystem.IsLinux() ? Path : Path.ToUpperInvariant());
}
