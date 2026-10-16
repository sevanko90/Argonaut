using System;

namespace Argonaut.Ui.TableGrid;

/// <summary>
/// Where a grid's columns are across the page: each one's width and left edge, which ones a
/// stretch of the page covers, and which column an x is in. What <see cref="TableSurface"/> draws
/// and hit-tests by - pure arithmetic over a prefix sum, so finding the columns on screen out of
/// thousands is a binary search rather than a walk.
///
/// A width the user set - by dragging an edge, or fitting a column to its content - is theirs:
/// <see cref="Reseed"/> (a new font) leaves it alone and moves only the columns still at the
/// width they opened at.
/// </summary>
public sealed class TableColumnLayout
{
    private readonly double[] widths;
    private readonly bool[] userSized;

    // lefts[i] is column i's left edge; lefts[Count] is the right edge of the last.
    private readonly double[] lefts;

    public TableColumnLayout(ReadOnlySpan<double> widths)
    {
        this.widths = widths.ToArray();
        this.userSized = new bool[widths.Length];
        this.lefts = new double[widths.Length + 1];
        Recompute(0);
    }

    public int Count => this.widths.Length;

    /// <summary>All the columns together, edge to edge.</summary>
    public double TotalWidth => this.lefts[^1];

    public double Width(int column) => this.widths[column];

    public double Left(int column) => this.lefts[column];

    public double Right(int column) => this.lefts[column + 1];

    /// <summary>Whether the user set this column's width.</summary>
    public bool IsUserSized(int column) => this.userSized[column];

    /// <summary>The column <paramref name="x"/> (measured from the first column's left edge) is
    /// in, or -1 past either end.</summary>
    public int ColumnAt(double x)
    {
        if (x < 0 || x >= TotalWidth || Count == 0)
            return -1;

        // The last column whose left edge is at or before x.
        int lo = 0, hi = Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (this.lefts[mid] <= x)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>The columns any part of [<paramref name="from"/>, <paramref name="to"/>) falls in,
    /// first to last - an empty range (Last below First) when it covers none.</summary>
    public (int First, int Last) Covering(double from, double to)
    {
        if (Count == 0 || to <= 0 || from >= TotalWidth || to <= from)
            return (0, -1);

        return (ColumnAt(Math.Max(0, from)), ColumnAt(Math.Min(to, TotalWidth) - 0.001));
    }

    /// <summary>The column whose right edge is within <paramref name="tolerance"/> of
    /// <paramref name="x"/> - the one a drag there resizes - or -1. The nearer edge wins where two
    /// are close.</summary>
    public int EdgeNear(double x, double tolerance)
    {
        int best = -1;
        double bestDistance = tolerance;
        int around = ColumnAt(Math.Clamp(x, 0, Math.Max(0, TotalWidth - 0.001)));
        for (int c = Math.Max(0, around - 1); c <= Math.Min(Count - 1, around + 1); c++)
        {
            double distance = Math.Abs(Right(c) - x);
            if (distance <= bestDistance)
            {
                best = c;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Sets a width the user chose. Never narrower than <paramref name="minimum"/>, so an
    /// edge can always be grabbed again.</summary>
    public void SetUserWidth(int column, double width, double minimum)
    {
        this.widths[column] = Math.Max(minimum, width);
        this.userSized[column] = true;
        Recompute(column);
    }

    /// <summary>New opening widths - a new font changes what a character count measures - for the
    /// columns the user has not sized.</summary>
    public void Reseed(ReadOnlySpan<double> widths)
    {
        for (int c = 0; c < this.widths.Length && c < widths.Length; c++)
        {
            if (!this.userSized[c])
                this.widths[c] = widths[c];
        }

        Recompute(0);
    }

    private void Recompute(int from)
    {
        for (int c = from; c < this.widths.Length; c++)
            this.lefts[c + 1] = this.lefts[c] + this.widths[c];
    }
}
