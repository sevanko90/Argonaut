using System;

namespace Argonaut.Engine.Selection;

/// <summary>
/// One stretch of the file a selection shows: <see cref="Length"/> bytes from
/// <see cref="FileOffset"/>. <see cref="AddsNewline"/> says the stretch does not end in a newline
/// of its own - the file's last line has none, or a byte entry ended mid-line - so the view adds
/// one after it. Every run then ends a line, which keeps anything that reads one line from
/// running on into the next stretch.
///
/// For a selection of lines, the run is file lines <see cref="FirstLine"/> onwards,
/// <see cref="LineCount"/> of them; a selection of bytes counts no lines, and both are 0.
/// </summary>
public readonly record struct SelectedRun(long FileOffset, long Length, bool AddsNewline, long FirstLine, long LineCount)
{
    /// <summary>The bytes the run takes up in the view: its own, and the newline it adds.</summary>
    public long ViewLength => Length + (AddsNewline ? 1 : 0);
}

/// <summary>
/// Where in its file each part of a <see cref="SelectionFilter"/> is: the runs a selection view
/// shows, in file order. Found by <see cref="LineSelectionLocator"/>.
/// </summary>
public sealed class LineSelection
{
    private readonly SelectedRun[] runs;

    internal LineSelection(SelectionUnit unit, SelectedRun[] runs, int rangesPastEnd)
    {
        Unit = unit;
        this.runs = runs;
        RangesPastEnd = rangesPastEnd;

        foreach (var run in runs)
        {
            ViewLength += run.ViewLength;
            LineCount += run.LineCount;
        }
    }

    public SelectionUnit Unit { get; }

    /// <summary>The runs, in file order, none overlapping another.</summary>
    public ReadOnlySpan<SelectedRun> Runs => this.runs;

    /// <summary>The runs as the array a selection's sources share, never copied per source.</summary>
    internal SelectedRun[] RunArray => this.runs;

    /// <summary>How many of the filter's merged ranges start past the end of the file, and were
    /// dropped.</summary>
    public int RangesPastEnd { get; }

    /// <summary>The length of the view: every run, with the newlines the runs add.</summary>
    public long ViewLength { get; }

    /// <summary>How many lines the view holds - for a selection of lines; 0 for one of bytes,
    /// whose lines are not counted.</summary>
    public long LineCount { get; }

    /// <summary>Nothing in the file is selected.</summary>
    public bool IsEmpty => this.runs.Length == 0;
}
