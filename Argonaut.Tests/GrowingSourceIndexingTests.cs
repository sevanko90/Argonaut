using System.Text;
using System.Text.Json;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The three background indexers against a source whose bytes are still arriving. Each one used
/// to read <c>Length</c> once at entry and loop to it, which over a <see cref="GrowingByteSource"/>
/// stops at whatever had arrived when the scan started and then reports a *complete* index over a
/// partial document - the failure this whole pair of members exists to prevent. So every test
/// here asserts two things: the scan does not finish early while bytes are outstanding, and once
/// sealed it produces exactly what it produces over a fully-present source.
/// </summary>
public class GrowingSourceIndexingTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>Reveals the payload a slice at a time on a worker, then seals.</summary>
    private static Task DripAsync(GrowingByteSource source, int sliceLength, int slices)
        => Task.Run(async () =>
        {
            for (int i = 0; i < slices; i++)
            {
                await Task.Delay(10);
                source.Reveal(sliceLength);
            }

            await Task.Delay(10);
            source.Seal();
        });

    // ---- NDJSON line index -------------------------------------------------------------

    [Fact]
    public async Task FileOffsetIndex_IndexesEveryLineThatArrivesAfterTheScanStarted()
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 40).Select(i => $"{{\"n\":{i}}}")) + "\n");
        var source = new GrowingByteSource(payload, initiallyAvailable: 12);

        var index = FileOffsetIndex.StartIndexing(source);
        await DripAsync(source, payload.Length / 8, 8);
        await index.IndexingTask;

        Assert.Equal(40, index.LineCount);
        Assert.True(source.Waits > 0, "the scan never actually waited for more bytes");

        // Same spans a fully-present source produces.
        var settled = FileOffsetIndex.StartIndexing(new MemoryByteSource(payload));
        await settled.IndexingTask;
        Assert.Equal(settled.LineCount, index.LineCount);
        for (int i = 0; i < settled.LineCount; i++)
            Assert.Equal(settled.GetLineSpan(i), index.GetLineSpan(i));
    }

    [Fact]
    public async Task FileOffsetIndex_DoesNotFinishWhileBytesAreStillOutstanding()
    {
        byte[] payload = Encoding.UTF8.GetBytes("one\ntwo\nthree\n");
        var source = new GrowingByteSource(payload, initiallyAvailable: 4);

        var index = FileOffsetIndex.StartIndexing(source);
        await Task.Delay(Settle);

        Assert.False(index.IndexingTask.IsCompleted);
        Assert.False(index.IsComplete);

        source.Seal();
        await index.IndexingTask;
        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task FileOffsetIndex_RecordsTheTrailingNewlinelessLineOnlyOnceSettled()
    {
        // "b" has no newline: while more bytes could still arrive it is not a line yet, it is a
        // line whose newline has not turned up.
        byte[] payload = Encoding.UTF8.GetBytes("a\nb");
        var source = new GrowingByteSource(payload, initiallyAvailable: payload.Length);

        var index = FileOffsetIndex.StartIndexing(source);
        await Task.Delay(Settle);
        Assert.Equal(1, index.LineCount);

        source.Seal();
        await index.IndexingTask;

        Assert.Equal(2, index.LineCount);
        Assert.Equal(new FileLineSpan(2, 1), index.GetLineSpan(1));
    }

    // ---- JSON structure index ----------------------------------------------------------

    [Fact]
    public async Task JsonStructureIndex_IndexesEveryTokenThatArrivesAfterTheScanStarted()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            "{\"items\":[" + string.Join(',', Enumerable.Range(0, 60).Select(i => $"{{\"id\":{i},\"name\":\"row {i}\"}}")) + "]}");
        var source = new GrowingByteSource(payload, initiallyAvailable: 5);

        var index = JsonStructureIndex.StartIndexing(source);
        await DripAsync(source, payload.Length / 10, 10);
        await index.IndexingTask;

        var settled = JsonStructureIndex.StartIndexing(new MemoryByteSource(payload));
        await settled.IndexingTask;

        Assert.Null(index.Failure);
        Assert.True(source.Waits > 0, "the scan never actually waited for more bytes");
        Assert.Equal(settled.TokenCount, index.TokenCount);
        for (int i = 0; i < settled.TokenCount; i++)
            Assert.Equal(settled.GetToken(i), index.GetToken(i));
    }

    [Fact]
    public async Task JsonStructureIndex_WaitsWhenAWindowEndsPartwayThroughAToken()
    {
        // Cut inside the number 123456789 so the reader consumes nothing from the first window
        // and has to wait for the rest rather than treating it as a token too large to parse.
        byte[] payload = Encoding.UTF8.GetBytes("{\"value\":123456789}");
        var source = new GrowingByteSource(payload, initiallyAvailable: 13);

        var index = JsonStructureIndex.StartIndexing(source);
        await Task.Delay(Settle);
        Assert.False(index.IndexingTask.IsCompleted);

        source.Seal();
        await index.IndexingTask;

        Assert.Null(index.Failure);
        var number = index.GetToken(1);
        Assert.Equal(JsonTokenKind.Number, number.Kind);
        Assert.Equal("123456789", source.GetUtf8String(number.Offset, number.Length));
    }

    [Fact]
    public async Task JsonStructureIndex_ATruncatedDownloadFailsRatherThanReportingAPartialIndex()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}");
        var source = new GrowingByteSource(payload, initiallyAvailable: 6);

        // Sealed before the scan starts, so this is deterministically "the download ended at
        // byte 6" rather than a race between the seal and the first window.
        source.SealWhereItIs();

        var index = JsonStructureIndex.StartIndexing(source);
        try
        {
            await index.IndexingTask;
        }
        catch (JsonException)
        {
            // Malformed input faults the task and records the failure - see JsonFailureLocationTests.
        }

        Assert.NotNull(index.Failure);
    }

    // ---- Raw row index -----------------------------------------------------------------

    [Fact]
    public async Task RawSegmentIndex_ProducesTheSameRowsAsAFullyPresentSource()
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 50).Select(i => new string('x', 10 + (i % 37)))) + "\n");
        var source = new GrowingByteSource(payload, initiallyAvailable: 3);

        var index = RawSegmentIndex.StartIndexing(source, wrapWidth: 16);
        await DripAsync(source, payload.Length / 12, 12);
        await index.IndexingTask;

        var settled = RawSegmentIndex.StartIndexing(new MemoryByteSource(payload), wrapWidth: 16);
        await settled.IndexingTask;

        Assert.True(source.Waits > 0, "the scan never actually waited for more bytes");
        Assert.Equal(settled.RowCount, index.RowCount);
        for (int i = 0; i < settled.RowCount; i++)
            Assert.Equal(settled.GetRowInfo(i), index.GetRowInfo(i));
    }

    [Fact]
    public async Task RawSegmentIndex_DoesNotPublishARowThatOnlyLooksFinishedBecauseTheRestIsMissing()
    {
        // Wrap width 16 and a 20-character first line: at 12 bytes available the row looks like
        // a complete 12-byte one, and publishing it would freeze the wrong boundary into an
        // append-only index.
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 20) + "\n" + new string('y', 5) + "\n");
        var source = new GrowingByteSource(payload, initiallyAvailable: 12);

        var index = RawSegmentIndex.StartIndexing(source, wrapWidth: 16);
        await Task.Delay(Settle);
        Assert.Equal(0, index.RowCount);

        source.Seal();
        await index.IndexingTask;

        var settled = RawSegmentIndex.StartIndexing(new MemoryByteSource(payload), wrapWidth: 16);
        await settled.IndexingTask;

        Assert.Equal(settled.RowCount, index.RowCount);
        for (int i = 0; i < settled.RowCount; i++)
            Assert.Equal(settled.GetRowInfo(i), index.GetRowInfo(i));
    }

    // ---- teardown ----------------------------------------------------------------------

    [Fact]
    public async Task AScanWaitingForMoreBytesStillStopsWhenCancelled()
    {
        byte[] payload = Encoding.UTF8.GetBytes("one\ntwo\nthree\n");
        var source = new GrowingByteSource(payload, initiallyAvailable: 4);
        using var stopping = new CancellationTokenSource();

        var index = FileOffsetIndex.StartIndexing(source, cancellationToken: stopping.Token);
        await Task.Delay(Settle);
        Assert.False(index.IndexingTask.IsCompleted);

        stopping.Cancel();

        // The join is what IndexedSourceSession.Dispose does before releasing the source, so a
        // scan parked in WaitForLength must come back promptly or closing a streamed document
        // would hang the UI thread.
        var stopped = await Task.WhenAny(index.IndexingTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(index.IndexingTask, stopped);
    }
}
