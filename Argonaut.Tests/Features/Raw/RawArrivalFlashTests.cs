using Argonaut.Features.Raw;

namespace Argonaut.Tests.Features.Raw;

/// <summary>The arrival flash's timing: it arrives at once, holds long enough to be seen, then
/// fades to nothing and says it is over, so the surface stops asking for frames.</summary>
public sealed class RawArrivalFlashTests
{
    [Fact]
    public void ItIsFullStrengthOnArrival_AndHoldsBeforeFading()
    {
        Assert.Equal(1, RawArrivalFlash.StrengthAt(TimeSpan.Zero));
        Assert.Equal(1, RawArrivalFlash.StrengthAt(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void ItFadesSteadilyToNothing()
    {
        double previous = 1;
        for (int ms = 500; ms <= 1400; ms += 100)
        {
            double strength = RawArrivalFlash.StrengthAt(TimeSpan.FromMilliseconds(ms));
            Assert.InRange(strength, 0, previous);
            previous = strength;
        }

        Assert.Equal(0, RawArrivalFlash.StrengthAt(RawArrivalFlash.Duration));
        Assert.Equal(0, RawArrivalFlash.StrengthAt(RawArrivalFlash.Duration * 2));
    }

    [Fact]
    public void ThePulseRingOnlyShowsAtTheStart()
    {
        Assert.True(RawArrivalFlash.RingAt(TimeSpan.Zero).Opacity > 0);
        Assert.Equal(0, RawArrivalFlash.RingAt(TimeSpan.FromMilliseconds(600)).Opacity);
        Assert.True(RawArrivalFlash.RingAt(TimeSpan.FromMilliseconds(200)).Spread > RawArrivalFlash.RingAt(TimeSpan.Zero).Spread);
    }
}
