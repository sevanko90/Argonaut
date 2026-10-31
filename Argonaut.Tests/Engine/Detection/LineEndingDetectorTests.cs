using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Detection;

public class LineEndingDetectorTests
{
    [Theory]
    [InlineData("a\nb\nc\n", LineEndingStyle.Lf)]
    [InlineData("a\r\nb\r\nc", LineEndingStyle.CrLf)]
    [InlineData("a\rb\rc\r", LineEndingStyle.Cr)]
    [InlineData("a\nb\r\nc", LineEndingStyle.Mixed)]
    [InlineData("a\rb\nc", LineEndingStyle.Mixed)]
    [InlineData("no breaks at all", LineEndingStyle.None)]
    [InlineData("", LineEndingStyle.None)]
    public async Task ASettledDocument_IsClassifiedByWhatItHolds(string text, LineEndingStyle expected)
    {
        var tally = await Detect(text);

        Assert.Equal(expected, tally.Style);
        Assert.True(tally.WholeInput);
    }

    [Fact]
    public async Task EachKindOfBreak_IsCountedOnItsOwn()
    {
        var tally = await Detect("a\nb\r\nc\rd\r\ne\n");

        Assert.Equal(new LineEndingTally(Lf: 2, CrLf: 2, LoneCr: 1, SampledBytes: 12, WholeInput: true), tally);
    }

    [Fact]
    public async Task ACrLfSplitAcrossPieces_IsOneCrLf()
    {
        // Pieces of 2 put every "\r" at the end of a piece and its "\n" at the start of the next.
        var tally = await Detect("a\r\nb\r\nc\r\n", pieceSize: 2);

        Assert.Equal(LineEndingStyle.CrLf, tally.Style);
        Assert.Equal(3, tally.CrLf);
    }

    [Fact]
    public async Task OnlyTheSampleIsCounted()
    {
        string text = "a\r\n" + new string('x', 20) + "\n\n\n";

        var tally = await Detect(text, sampleLimit: 10);

        Assert.Equal(10, tally.SampledBytes);
        Assert.False(tally.WholeInput);
        Assert.Equal(LineEndingStyle.CrLf, tally.Style);
    }

    [Fact]
    public async Task ACrAtTheCut_IsNotCounted_WhenMoreFollows()
    {
        // The sample ends on the "\r" of the second break; its "\n" is past the cut.
        var tally = await Detect("ab\nc\r\nd", sampleLimit: 5);

        Assert.Equal(new LineEndingTally(Lf: 1, CrLf: 0, LoneCr: 0, SampledBytes: 5, WholeInput: false), tally);
    }

    [Fact]
    public async Task ACrAtTheEnd_IsALoneCr_WhenNothingFollows()
    {
        var tally = await Detect("a\rb\r");

        Assert.Equal(2, tally.LoneCr);
    }

    [Fact]
    public async Task TheSourceItOpens_IsReleased()
    {
        var origin = new TestOrigin(Encoding.ASCII.GetBytes("a\nb\n"), settledFromBirth: true);

        await new LineEndingDetector().DetectAsync(origin, CancellationToken.None);

        Assert.Equal(1, origin.Opened);
        Assert.Equal(1, origin.Released);
    }

    [Fact]
    public async Task AStreamedDocument_IsWaitedForUntilTheSampleHasArrived()
    {
        byte[] all = Encoding.ASCII.GetBytes("a\r\nb\r\n" + new string('x', 100));
        var origin = new TestOrigin(all, settledFromBirth: false, initiallyAvailable: 2);

        var detecting = new LineEndingDetector(sampleLimit: 6).DetectAsync(origin, CancellationToken.None);
        Assert.False(detecting.IsCompleted);

        origin.Growing.Reveal(10);
        var tally = await detecting;

        Assert.Equal(new LineEndingTally(Lf: 0, CrLf: 2, LoneCr: 0, SampledBytes: 6, WholeInput: false), tally);
    }

    [Fact]
    public async Task AStreamedDocumentThatSettlesShortOfTheSample_IsCountedWhole()
    {
        byte[] all = Encoding.ASCII.GetBytes("a\nb\nc\n");
        var origin = new TestOrigin(all, settledFromBirth: false, initiallyAvailable: 2);

        var detecting = new LineEndingDetector(sampleLimit: 1000).DetectAsync(origin, CancellationToken.None);
        origin.Growing.Seal();
        var tally = await detecting;

        Assert.Equal(3, tally.Lf);
        Assert.True(tally.WholeInput);
    }

    [Fact]
    public async Task ASourceThatSettlesBetweenTheReads_IsNotTakenAsEndingAtTheOldLength()
    {
        byte[] all = Encoding.ASCII.GetBytes("a\nb\nc\nd\n");
        var origin = new TestOrigin(all, settledFromBirth: false, initiallyAvailable: 2);
        origin.Growing.SettleBehindTheNextLengthRead();

        var tally = await new LineEndingDetector(sampleLimit: 1000).DetectAsync(origin, CancellationToken.None);

        Assert.Equal(all.Length, tally.SampledBytes);
        Assert.Equal(4, tally.Lf);
    }

    [Fact]
    public async Task AStoppedDetection_EndsCancelled()
    {
        var origin = new TestOrigin(new byte[10], settledFromBirth: false, initiallyAvailable: 2);
        using var stopping = new CancellationTokenSource();

        var detecting = new LineEndingDetector(sampleLimit: 100).DetectAsync(origin, stopping.Token);
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detecting);
    }

    private static Task<LineEndingTally> Detect(string text, int sampleLimit = LineEndingDetector.DefaultSampleLimit, int pieceSize = 4096)
        => new LineEndingDetector(sampleLimit)
            .DetectAsync(new TestOrigin(Encoding.ASCII.GetBytes(text), settledFromBirth: true, pieceSize: pieceSize), CancellationToken.None);

    /// <summary>
    /// A document whose length grows through a <see cref="GrowingByteSource"/>, and whose
    /// sub-ranges are served in pieces and counted as they are opened and released.
    /// </summary>
    private sealed class TestOrigin : IByteOrigin
    {
        private readonly byte[] all;
        private readonly int pieceSize;
        private readonly MemoryByteOrigin kept = new([], "test");

        public TestOrigin(byte[] all, bool settledFromBirth, long initiallyAvailable = 0, int pieceSize = 4096)
        {
            this.all = all;
            this.pieceSize = pieceSize;
            Growing = new GrowingByteSource(all, settledFromBirth ? all.Length : initiallyAvailable);
            if (settledFromBirth)
                Growing.Seal();
        }

        public GrowingByteSource Growing { get; }
        public int Opened { get; private set; }
        public int Released { get; private set; }

        public string DisplayName => "test";
        public string? Path => null;
        public long AvailableLength => Growing.AvailableLength;
        public bool LengthSettled => Growing.LengthSettled;
        public KeptIndexes KeptIndexes => this.kept.KeptIndexes;
        public void WaitForLength(long atLeast, CancellationToken cancellationToken) => Growing.WaitForLength(atLeast, cancellationToken);
        public IByteSource Open() => throw new NotSupportedException();

        public IByteSource OpenRange(long offset, long length)
        {
            Opened++;
            return new ReleasedSource(new SplitByteSource(this.all.AsSpan((int)offset, (int)length).ToArray(), this.pieceSize), () => Released++);
        }

        public void Dispose() => this.kept.Dispose();
    }

    private sealed class ReleasedSource(IByteSource inner, Action onRelease) : IByteSource, IDisposable
    {
        public long AvailableLength => inner.AvailableLength;
        public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength) => inner.GetContiguousSpan(offset, maxLength);
        public int CopyTo(long offset, Span<byte> destination) => inner.CopyTo(offset, destination);
        public void Dispose() => onRelease();
    }
}
