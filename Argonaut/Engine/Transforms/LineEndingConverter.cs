using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Progress;

namespace Argonaut.Engine.Transforms;

/// <inheritdoc cref="ILineEndingConverter"/>
public sealed class LineEndingConverter : ILineEndingConverter
{
    /// <summary>How much converted output is gathered before each write - a save's stage stream
    /// is unbuffered, and a write per line would be a system call per line.</summary>
    public const int DefaultOutputBufferBytes = 1024 * 1024;

    private readonly int outputBufferBytes;

    public LineEndingConverter(int outputBufferBytes = DefaultOutputBufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputBufferBytes, 2);
        this.outputBufferBytes = outputBufferBytes;
    }

    public long MeasureOutput(IByteSource source, LineEndingStyle target, CancellationToken stopping)
    {
        RequireBreak(target);
        long length = SettledLength(source);
        var counts = LineBreakCounts.Count(source, length, stopping);
        long lf = counts.Lf - counts.CrLf;
        long loneCr = counts.Cr - counts.CrLf;
        // To a one-byte break each CRLF loses a byte; to CRLF each one-byte break gains one.
        return target == LineEndingStyle.CrLf ? length + lf + loneCr : length - counts.CrLf;
    }

    public long WriteConverted(IByteSource source, LineEndingStyle target, Stream destination,
        IProgressReporter? progress, CancellationToken stopping, long trackedOffset)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RequireBreak(target);
        long length = SettledLength(source);

        using var output = new BatchedWriter(destination, this.outputBufferBytes);
        var newline = LineEndingBytes.Of(target);
        long? tracked = null;
        bool pendingCr = false;
        long pendingCrAt = 0;
        long read = 0;

        while (read < length)
        {
            stopping.ThrowIfCancellationRequested();
            var run = source.GetContiguousSpan(read, (int)Math.Min(ByteSourceReading.WriteChunkBytes, length - read));
            if (run.IsEmpty)
                throw new IOException($"The document ended at byte {read:N0} of {length:N0} while it was being written.");

            int i = 0;
            if (pendingCr)
            {
                // The CR that ended the previous run, and the LF that may complete it here.
                Track(ref tracked, trackedOffset, pendingCrAt, run[0] == (byte)'\n' ? 2 : 1, output.Written, isBreak: true);
                output.Write(newline);
                if (run[0] == (byte)'\n')
                    i = 1;
                pendingCr = false;
            }

            while (i < run.Length)
            {
                int found = run[i..].IndexOfAny((byte)'\r', (byte)'\n');
                if (found < 0)
                {
                    Track(ref tracked, trackedOffset, read + i, run.Length - i, output.Written, isBreak: false);
                    output.Write(run[i..]);
                    break;
                }

                if (found > 0)
                {
                    Track(ref tracked, trackedOffset, read + i, found, output.Written, isBreak: false);
                    output.Write(run.Slice(i, found));
                }

                int at = i + found;
                if (run[at] == (byte)'\n')
                {
                    Track(ref tracked, trackedOffset, read + at, 1, output.Written, isBreak: true);
                    output.Write(newline);
                    i = at + 1;
                }
                else if (at + 1 < run.Length)
                {
                    int width = run[at + 1] == (byte)'\n' ? 2 : 1;
                    Track(ref tracked, trackedOffset, read + at, width, output.Written, isBreak: true);
                    output.Write(newline);
                    i = at + width;
                }
                else
                {
                    // A CR ending the run: whether it is half a CRLF is the next run's first byte.
                    pendingCr = true;
                    pendingCrAt = read + at;
                    i = at + 1;
                }
            }

            read += run.Length;
            progress?.Report("Saving", read, length);
        }

        if (pendingCr)
        {
            Track(ref tracked, trackedOffset, pendingCrAt, 1, output.Written, isBreak: true);
            output.Write(newline);
        }

        output.Flush();
        return tracked ?? output.Written;
    }

    public void ConvertPaste(ReadOnlySpan<byte> text, LineEndingStyle target, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        RequireBreak(target);
        var newline = LineEndingBytes.Of(target);

        int start = 0;
        while (start < text.Length)
        {
            int lf = text[start..].IndexOf((byte)'\n');
            if (lf < 0)
            {
                output.Write(text[start..]);
                return;
            }

            int at = start + lf;
            int breakStart = at > start && text[at - 1] == (byte)'\r' ? at - 1 : at;
            output.Write(text[start..breakStart]);
            output.Write(newline);
            start = at + 1;
        }
    }

    /// <summary>
    /// Records where <paramref name="trackedOffset"/> lands, the first time a piece of the source
    /// covering it is written: inside literal text it keeps its distance from the piece's start;
    /// anywhere in a break it lands at the start of the break written in its place.
    /// </summary>
    private static void Track(ref long? tracked, long trackedOffset, long pieceStart, int pieceLength,
        long writtenSoFar, bool isBreak)
    {
        if (tracked is not null || trackedOffset >= pieceStart + pieceLength)
            return;

        tracked = writtenSoFar + (isBreak ? 0 : Math.Max(0, trackedOffset - pieceStart));
    }

    private static void RequireBreak(LineEndingStyle target)
    {
        if (target is not (LineEndingStyle.Lf or LineEndingStyle.CrLf or LineEndingStyle.Cr))
            throw new ArgumentOutOfRangeException(nameof(target), target, "A document can only be converted to LF, CRLF or CR.");
    }

    private static long SettledLength(IByteSource source)
    {
        if (!source.LengthSettled)
            throw new InvalidOperationException("Only a source whose length is settled can be converted whole.");

        return source.AvailableLength;
    }

    /// <summary>Gathers small writes into one pooled buffer; a span larger than the buffer goes
    /// straight through once what is gathered has been written ahead of it.</summary>
    private sealed class BatchedWriter(Stream destination, int capacity) : IDisposable
    {
        private byte[]? buffer = ArrayPool<byte>.Shared.Rent(capacity);
        private int used;

        public long Written { get; private set; }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            var into = this.buffer!;
            if (bytes.Length > into.Length - this.used)
            {
                Flush();
                if (bytes.Length >= into.Length)
                {
                    destination.Write(bytes);
                    Written += bytes.Length;
                    return;
                }
            }

            bytes.CopyTo(into.AsSpan(this.used));
            this.used += bytes.Length;
            Written += bytes.Length;
        }

        public void Flush()
        {
            if (this.used == 0)
                return;

            destination.Write(this.buffer!, 0, this.used);
            this.used = 0;
        }

        public void Dispose()
        {
            if (this.buffer is { } rented)
            {
                this.buffer = null;
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
