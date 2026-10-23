using System;
using System.Buffers;
using System.Threading;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Transforms;

/// <summary>
/// Walks a settled source line by line. A line that sits inside one contiguous run is returned
/// as a view of the source, with no copy; only a line that straddles a run boundary, or is longer
/// than one scan chunk, is gathered into a pooled buffer - so the memory held is the longest
/// such line, never the document.
///
/// Lines split on <c>\n</c>, with a trailing <c>\r</c> removed. A final newline does not start
/// an empty last line, so <c>"a\nb\n"</c> is two lines - and <see cref="EndedWithNewline"/>
/// records that it was there, for a transform that wants to keep it.
/// </summary>
internal sealed class TransformLineReader : IDisposable
{
    internal const int ScanChunkBytes = 64 * 1024;

    /// <summary>What trimming a line removes: spaces and tabs, never line breaks.</summary>
    public static ReadOnlySpan<byte> SpacesAndTabs => " \t"u8;

    private readonly IByteSource source;
    private readonly long length;
    private long position;
    private byte[]? gathered;
    private bool sawTerminator;
    private bool crlf;

    /// <param name="source">Must be settled: a transform runs over a finished document (a
    /// paste, or an edited one, which needs a completed scan), so the length is read once here,
    /// as <see cref="ByteSourceReading.WriteTo"/> does for the same reason.</param>
    public TransformLineReader(IByteSource source)
    {
        if (!source.LengthSettled)
            throw new InvalidOperationException("A transform runs over a document that has finished arriving.");

        this.source = source;
        this.length = source.AvailableLength;
    }

    /// <summary>1-based number of the line last returned.</summary>
    public int LineNumber { get; private set; }

    /// <summary>The terminator the input uses, judged by its first line: <c>\r\n</c> or
    /// <c>\n</c>. Output a transform writes between lines uses the same.</summary>
    public ReadOnlySpan<byte> NewLine => this.crlf ? "\r\n"u8 : "\n"u8;

    /// <summary>True once the last line has been read and it had a terminator.</summary>
    public bool EndedWithNewline { get; private set; }

    /// <summary>The next line, without its terminator. The span is valid only until the next
    /// call.</summary>
    public bool TryRead(out ReadOnlySpan<byte> line)
    {
        if (this.position >= this.length)
        {
            line = default;
            return false;
        }

        LineNumber++;
        var run = this.source.GetContiguousSpan(this.position, (int)Math.Min(ScanChunkBytes, this.length - this.position));
        int newline = run.IndexOf((byte)'\n');
        if (newline >= 0)
        {
            this.position += newline + 1;
            line = Terminated(run[..newline]);
            return true;
        }

        line = Gather(run);
        return true;
    }

    private ReadOnlySpan<byte> Gather(ReadOnlySpan<byte> run)
    {
        int filled = 0;
        while (true)
        {
            Append(run, ref filled);
            this.position += run.Length;
            if (this.position >= this.length)
            {
                EndedWithNewline = false;
                return this.gathered.AsSpan(0, filled);
            }

            run = this.source.GetContiguousSpan(this.position, (int)Math.Min(ScanChunkBytes, this.length - this.position));
            if (run.IsEmpty)
                throw new InvalidOperationException($"The document ended at byte {this.position:N0} of {this.length:N0}.");

            int newline = run.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                Append(run[..newline], ref filled);
                this.position += newline + 1;
                return Terminated(this.gathered.AsSpan(0, filled));
            }
        }
    }

    private void Append(ReadOnlySpan<byte> run, ref int filled)
    {
        if (this.gathered is null || this.gathered.Length - filled < run.Length)
        {
            var larger = ArrayPool<byte>.Shared.Rent(Math.Max(ScanChunkBytes, (filled + run.Length) * 2));
            if (this.gathered is not null)
            {
                this.gathered.AsSpan(0, filled).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(this.gathered);
            }

            this.gathered = larger;
        }

        run.CopyTo(this.gathered.AsSpan(filled));
        filled += run.Length;
    }

    private ReadOnlySpan<byte> Terminated(ReadOnlySpan<byte> line)
    {
        bool hadReturn = !line.IsEmpty && line[^1] == (byte)'\r';
        if (!this.sawTerminator)
        {
            this.sawTerminator = true;
            this.crlf = hadReturn;
        }

        EndedWithNewline = this.position >= this.length;
        return hadReturn ? line[..^1] : line;
    }

    /// <summary>Checks for a stop request every thousand or so lines - often enough to stop a
    /// run promptly, rarely enough to cost nothing per line.</summary>
    public void ThrowIfStopping(CancellationToken stopping)
    {
        if (LineNumber % 1024 == 0)
            stopping.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (this.gathered is not null)
        {
            ArrayPool<byte>.Shared.Return(this.gathered);
            this.gathered = null;
        }
    }
}
