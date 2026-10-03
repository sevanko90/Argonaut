namespace Argonaut.Engine.Bytes;

/// <summary>
/// Keeps a sequential scan's next bytes on their way from disk while it works on the current
/// ones, by hinting the window ahead of it (<see cref="IByteSource.Prefetch"/>). A scan calls
/// <see cref="Reached"/> with where it is, as often as it likes: all but one call in tens of
/// thousands is a single comparison, so a per-row loop can afford it.
///
/// Between one and two windows are always hinted ahead of the scan, so the OS is fetching the
/// next window while the scan reads the current one.
/// </summary>
public struct ReadAhead
{
    /// <summary>How much each hint covers. Big enough that the hints are rare, small enough that
    /// what a cancelled scan leaves fetched is bounded.</summary>
    public const long Window = 64L * 1024 * 1024;

    private long hintedTo;

    /// <summary>The scan is at <paramref name="position"/>. Once it is within a window of what has
    /// been hinted, hint on to two windows ahead - so each hint covers about a window.</summary>
    public void Reached(IByteSource source, long position)
    {
        if (position + Window <= this.hintedTo)
            return;

        long from = System.Math.Max(position, this.hintedTo);
        long to = position + 2 * Window;
        source.Prefetch(from, to - from);
        this.hintedTo = to;
    }
}
