using System;
using System.Globalization;

namespace Argonaut.Engine.Indexing.Lines;

/// <summary>
/// One stretch of a document that lines up with a stretch of its file: view line
/// <see cref="ViewLine"/> (1-based) at view offset <see cref="ViewOffset"/> is file line
/// <see cref="FileLine"/> at file offset <see cref="FileOffset"/>, and each line and byte after it
/// follows one for one up to the next run. Runs are whole lines, so a run's first byte is always
/// a line's first byte.
/// </summary>
public readonly record struct LineRun(long ViewLine, long FileLine, long ViewOffset, long FileOffset);

/// <summary>
/// How a document's lines and byte offsets are numbered where the user reads them - a gutter, a
/// caret readout, a status line. Every index counts from the start of the bytes it was built
/// over; this maps that onto what is shown. For a whole file the two agree. For an excerpt they
/// differ by where it starts in the file, and the user picks between the file's own numbers and
/// counting from the excerpt's start. A document made of several separate stretches of a file
/// has one <see cref="LineRun"/> per stretch.
///
/// An excerpt taken by bytes does not know which file line it starts on, so numbered by its file
/// it labels each line by the file offset it starts at instead (<see cref="LabelsLineStarts"/>).
///
/// Only display goes through this: indexes, reveals and search stay relative to the document.
/// Immutable, so one instance can be shared by every view of the document.
/// </summary>
public sealed class LineNumbering : IEquatable<LineNumbering>
{
    private readonly LineRun[] runs;

    /// <summary>The view offset of the document's last byte, which has the widest label; only
    /// meaningful when <see cref="LabelsLineStarts"/>.</summary>
    private readonly long lastOffset;

    private LineNumbering(LineRun[] runs, bool labelsLineStarts = false, long lastOffset = 0)
    {
        this.runs = runs;
        LabelsLineStarts = labelsLineStarts;
        this.lastOffset = lastOffset;
    }

    /// <summary>Line 1 at offset 0 - a whole document, or an excerpt counted from its start.</summary>
    public static LineNumbering FromStart { get; } = Starting(1, 0);

    /// <summary>A document that is one stretch of its file, starting at line
    /// <paramref name="firstLine"/> and byte <paramref name="firstOffset"/>.</summary>
    public static LineNumbering Starting(long firstLine, long firstOffset)
        => new([new LineRun(1, firstLine, 0, firstOffset)]);

    /// <summary>A document of <paramref name="length"/> bytes starting at byte
    /// <paramref name="firstOffset"/> of its file, whose lines are labelled by the file offset
    /// they start at.</summary>
    public static LineNumbering ByLineStarts(long firstOffset, long length)
        => new([new LineRun(1, 1, 0, firstOffset)], labelsLineStarts: true, lastOffset: Math.Max(0, length - 1));

    /// <summary>
    /// A document made of <paramref name="runs"/>, in view order. The first starts at view line 1
    /// and view offset 0, and each later one starts at a later view line and a later view offset.
    /// The file numbers are not checked against each other: a run may number from anywhere.
    /// </summary>
    public static LineNumbering FromRuns(ReadOnlySpan<LineRun> runs)
    {
        if (runs.IsEmpty)
            throw new ArgumentException("A numbering needs at least one run.", nameof(runs));
        if (runs[0].ViewLine != 1 || runs[0].ViewOffset != 0)
            throw new ArgumentException("The first run must start at view line 1, offset 0.", nameof(runs));

        for (int i = 1; i < runs.Length; i++)
        {
            if (runs[i].ViewLine <= runs[i - 1].ViewLine || runs[i].ViewOffset <= runs[i - 1].ViewOffset)
                throw new ArgumentException($"Run {i} does not start after run {i - 1}.", nameof(runs));
        }

        return new LineNumbering(runs.ToArray());
    }

    /// <summary>The runs, in view order; one for a whole document or a single excerpt.</summary>
    public ReadOnlySpan<LineRun> Runs => runs;

    /// <summary>True labels a line by the file offset it starts at rather than by its number.</summary>
    public bool LabelsLineStarts { get; }

    /// <summary>
    /// What a gutter shows for 1-based line <paramref name="lineNumber"/>, which starts at view
    /// offset <paramref name="lineStart"/>: its number, or its file offset grouped the reader's
    /// way (an offset runs to ten digits, which ungrouped is hard to read).
    /// </summary>
    public string GutterLabel(long lineNumber, long lineStart)
        => LabelsLineStarts
            ? Offset(lineStart).ToString("N0", CultureInfo.CurrentCulture)
            : Line(lineNumber).ToString(CultureInfo.InvariantCulture);

    /// <summary>The widest label a gutter labelling line starts will show, to size it by; null
    /// when lines are labelled by number, whose widest depends on how many there are.</summary>
    public string? WidestLineStartLabel
        => LabelsLineStarts ? Offset(this.lastOffset).ToString("N0", CultureInfo.CurrentCulture) : null;

    /// <summary>The number shown for 1-based line <paramref name="lineNumber"/> of the document.</summary>
    public long Line(long lineNumber)
    {
        ref readonly var run = ref runs[RunIndexByLine(lineNumber)];
        return run.FileLine + (lineNumber - run.ViewLine);
    }

    /// <summary>The offset shown for byte <paramref name="offset"/> of the document.</summary>
    public long Offset(long offset)
    {
        ref readonly var run = ref runs[RunIndexByOffset(offset)];
        return run.FileOffset + (offset - run.ViewOffset);
    }

    // The last run starting at or before the key. A key before the first run (line 0 or less)
    // takes the first, so it extends backwards the way a single run always has.
    private int RunIndexByLine(long lineNumber)
    {
        int lo = 0, hi = runs.Length - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo + 1) >> 1);
            if (runs[mid].ViewLine <= lineNumber)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    private int RunIndexByOffset(long offset)
    {
        int lo = 0, hi = runs.Length - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo + 1) >> 1);
            if (runs[mid].ViewOffset <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    public bool Equals(LineNumbering? other)
        => other is not null && (ReferenceEquals(this, other)
            || (LabelsLineStarts == other.LabelsLineStarts && this.lastOffset == other.lastOffset
                && runs.AsSpan().SequenceEqual(other.runs)));

    public override bool Equals(object? obj) => Equals(obj as LineNumbering);

    public override int GetHashCode() => HashCode.Combine(runs.Length, runs[0], runs[^1], LabelsLineStarts);
}
