using System;
using System.IO;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// Which bytes an origin held at one moment, cheaply enough to ask again later: its length, and
/// for a file its last-write time. Two equal versions mean an index built over the first still
/// describes the second. A file can be edited by another program or replaced by a save while it
/// is open, and an index kept past that would describe bytes that are no longer there - on a
/// shorter file, bytes past its end.
/// </summary>
public readonly record struct ByteOriginVersion(long Length, DateTime? LastWriteUtc)
{
    /// <summary>The version <paramref name="origin"/> is at now, or null while its bytes are still
    /// arriving - there is no final version of a document that is still growing.</summary>
    public static ByteOriginVersion? Of(IByteOrigin origin)
    {
        if (!origin.LengthSettled)
            return null;

        if (origin.Path is not { } path)
            return new ByteOriginVersion(origin.AvailableLength, null);

        var file = new FileInfo(path);
        return file.Exists ? new ByteOriginVersion(file.Length, file.LastWriteTimeUtc) : null;
    }
}
