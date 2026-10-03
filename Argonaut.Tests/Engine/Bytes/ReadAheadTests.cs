using Argonaut.Engine.Bytes;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Bytes;

public sealed class ReadAheadTests
{
    [Fact]
    public void TheFirstCall_HintsTwoWindowsFromWhereTheScanIs()
    {
        var source = new PrefetchRecordingSource([]);
        var readAhead = new ReadAhead();

        readAhead.Reached(source, 0);

        Assert.Equal(new[] { (0L, 2 * ReadAhead.Window) }, source.Hints);
    }

    [Fact]
    public void AScanThatKeepsGoing_IsHintedContiguouslyAndRarely()
    {
        var source = new PrefetchRecordingSource([]);
        var readAhead = new ReadAhead();

        // A gigabyte in row-sized steps: a hundred million calls' worth of positions, sampled.
        for (long position = 0; position < 1L << 30; position += 160)
            readAhead.Reached(source, position);

        // Each hint picks up exactly where the last ended, so nothing is hinted twice or skipped.
        for (int i = 1; i < source.Hints.Count; i++)
            Assert.Equal(source.Hints[i - 1].Offset + source.Hints[i - 1].Length, source.Hints[i].Offset);

        // And there are few of them: about one per window scanned.
        Assert.InRange(source.Hints.Count, (1L << 30) / ReadAhead.Window - 1, (1L << 30) / ReadAhead.Window + 2);
        Assert.True(source.Hints[^1].Offset + source.Hints[^1].Length >= (1L << 30) + ReadAhead.Window);
    }

    [Fact]
    public void ASeekForward_HintsFromTheNewPositionNotTheOldWindow()
    {
        var source = new PrefetchRecordingSource([]);
        var readAhead = new ReadAhead();

        readAhead.Reached(source, 0);
        readAhead.Reached(source, 10 * ReadAhead.Window);

        Assert.Equal(10 * ReadAhead.Window, source.Hints[^1].Offset);
    }
}
