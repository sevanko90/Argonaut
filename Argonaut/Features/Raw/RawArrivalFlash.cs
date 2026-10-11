using System;

namespace Argonaut.Features.Raw;

/// <summary>
/// The timing of the wash the raw view flashes where a jump in landed. In a wall of packed text a
/// thin caret is easy to lose; the flash says where to look, then gets out of the way. Full
/// strength at once, held long enough for the eye to get there, then faded out - with a ring that
/// spreads and fades over the first moment, which is what reads as arrival rather than a highlight.
/// </summary>
public static class RawArrivalFlash
{
    /// <summary>How long the flash lasts, start to nothing.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1400);

    /// <summary>How long it stays at full strength before fading.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(450);

    /// <summary>How long the pulse ring takes to spread out and vanish.</summary>
    private static readonly TimeSpan RingDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>How far the ring spreads beyond the wash, in pixels - kept inside a row's leading
    /// so the row's clip does not cut it.</summary>
    private const double RingSpread = 3;

    /// <summary>Bytes washed from the landing point on, so the flash marks a place and a
    /// direction rather than one glyph. A byte count rather than characters so it can run on
    /// across a wrap: a landing at the end of a row continues at the start of the next.</summary>
    public const int Bytes = 24;

    /// <summary>The wash's strength, 1 to 0, at <paramref name="elapsed"/> since arrival.</summary>
    public static double StrengthAt(TimeSpan elapsed)
    {
        if (elapsed <= Hold)
            return 1;
        if (elapsed >= Duration)
            return 0;

        // Ease out: quick to start fading, gentle at the end.
        double progress = (elapsed - Hold) / (Duration - Hold);
        return (1 - progress) * (1 - progress);
    }

    /// <summary>The pulse ring at <paramref name="elapsed"/>: how far beyond the wash it has
    /// spread, and how visible it still is.</summary>
    public static (double Spread, double Opacity) RingAt(TimeSpan elapsed)
    {
        if (elapsed >= RingDuration)
            return (RingSpread, 0);

        double progress = Math.Max(0, elapsed / RingDuration);
        return (RingSpread * progress, 1 - progress);
    }
}
