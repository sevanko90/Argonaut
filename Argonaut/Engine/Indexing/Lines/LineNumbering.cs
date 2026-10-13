namespace Argonaut.Engine.Indexing.Lines;

/// <summary>
/// How a document's lines and byte offsets are numbered where the user reads them - a gutter, a
/// caret readout, a status line. Every index counts from the start of the bytes it was built
/// over; this maps that onto what is shown. For a whole file the two agree. For an excerpt they
/// differ by where it starts in the file, and the user picks between the file's own numbers and
/// counting from the excerpt's start.
///
/// Only display goes through this: indexes, reveals and search stay relative to the document.
/// </summary>
public readonly record struct LineNumbering(long FirstLine, long FirstOffset)
{
    /// <summary>Line 1 at offset 0 - a whole document, or an excerpt counted from its start.</summary>
    public static LineNumbering FromStart { get; } = new(1, 0);

    /// <summary>The number shown for 1-based line <paramref name="lineNumber"/> of the document.</summary>
    public long Line(long lineNumber) => FirstLine - 1 + lineNumber;

    /// <summary>The offset shown for byte <paramref name="offset"/> of the document.</summary>
    public long Offset(long offset) => FirstOffset + offset;
}
