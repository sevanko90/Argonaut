using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// An <see cref="IByteSource"/> whose bytes arrive after construction - the streamed-download
/// case, made deterministic. The whole payload is held from the start but only the first
/// <see cref="AvailableLength"/> bytes are visible, so a test drives arrival explicitly with
/// <see cref="Reveal"/> and <see cref="Seal"/> instead of racing a real network.
///
/// Its reason to exist is that a scanner bug here is invisible against every other source: a
/// loop that snapshots the length once still indexes a mapping or an array perfectly, and only
/// silently under-indexes when the data was still arriving.
/// </summary>
internal sealed class GrowingByteSource : IByteSource
{
    private readonly byte[] all;
    private readonly object gate = new();
    private long available;
    private bool settled;

    public GrowingByteSource(byte[] all, long initiallyAvailable = 0)
    {
        this.all = all;
        this.available = Math.Clamp(initiallyAvailable, 0, all.Length);
    }

    public long AvailableLength
    {
        get { lock (this.gate) return this.available; }
    }

    public bool LengthSettled
    {
        get { lock (this.gate) return this.settled; }
    }

    /// <summary>Number of times a scanner actually blocked waiting for more - so a test can
    /// assert the growth path was exercised rather than the data happening to be there.</summary>
    public int Waits { get; private set; }

    /// <summary>Makes <paramref name="count"/> more bytes visible and wakes any waiter.</summary>
    public void Reveal(long count)
    {
        lock (this.gate)
        {
            this.available = Math.Min(this.all.Length, this.available + count);
            Monitor.PulseAll(this.gate);
        }
    }

    /// <summary>Reveals everything left and declares the length final.</summary>
    public void Seal()
    {
        lock (this.gate)
        {
            this.available = this.all.Length;
            this.settled = true;
            Monitor.PulseAll(this.gate);
        }
    }

    /// <summary>Declares the length final without revealing the rest - a truncated download.</summary>
    public void SealWhereItIs()
    {
        lock (this.gate)
        {
            this.settled = true;
            Monitor.PulseAll(this.gate);
        }
    }

    public void WaitForLength(long atLeast, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.Waits++;
            while (this.available < atLeast && !this.settled)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A bounded wait rather than an indefinite one so a cancelled scan cannot wedge
                // a test run if it is cancelled between the check and the wait.
                Monitor.Wait(this.gate, TimeSpan.FromMilliseconds(25));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
    {
        long limit = AvailableLength;
        return offset < 0 || offset >= limit || maxLength <= 0
            ? ReadOnlySpan<byte>.Empty
            : this.all.AsSpan((int)offset, (int)Math.Min(maxLength, limit - offset));
    }

    public int CopyTo(long offset, Span<byte> destination)
    {
        var span = GetContiguousSpan(offset, destination.Length);
        span.CopyTo(destination);
        return span.Length;
    }
}
