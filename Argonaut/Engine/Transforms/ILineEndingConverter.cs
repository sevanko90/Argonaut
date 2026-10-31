using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Progress;

namespace Argonaut.Engine.Transforms;

/// <summary>
/// Rewrites a document's line breaks to one kind. Streams: a whole document is converted on its way
/// into a file, never into memory, so a multi-GB document costs a buffer, not its size. Every
/// member that reads a whole document is a background caller's.
/// </summary>
public interface ILineEndingConverter
{
    /// <summary>
    /// The length <paramref name="source"/> has once every break in it is
    /// <paramref name="target"/> - what <see cref="WriteConverted"/> will write, known before
    /// writing so a save can stage exactly that much. A full read of a settled source.
    /// </summary>
    long MeasureOutput(IByteSource source, LineEndingStyle target, CancellationToken stopping);

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="destination"/> with every break - CRLF,
    /// LF or a lone CR - written as <paramref name="target"/>. Returns where the byte at
    /// <paramref name="trackedOffset"/> of the source lands in what was written, so a caret can
    /// follow its text across the conversion.
    /// </summary>
    long WriteConverted(IByteSource source, LineEndingStyle target, Stream destination,
        IProgressReporter? progress, CancellationToken stopping, long trackedOffset);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="output"/> with its CRLF and LF breaks as
    /// <paramref name="target"/>, for text pasted into a document that uses that convention. A lone
    /// CR is left as it is: in pasted text it is rarely a line break someone meant.
    /// </summary>
    void ConvertPaste(ReadOnlySpan<byte> text, LineEndingStyle target, IBufferWriter<byte> output);
}
