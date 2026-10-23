using System;
using System.Buffers;
using System.Buffers.Text;
using System.Threading;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Transforms;

/// <summary>A fixed encoding the raw view offers both ways. Unlike a <see cref="Recipe"/> there
/// is nothing to configure, so these are built-in operations rather than saved files.</summary>
public enum TextCodec
{
    /// <summary>The body of a JSON string - escaped without the surrounding quotes.</summary>
    JsonString,

    /// <summary>Percent-encoding of every byte outside RFC 3986's unreserved set.</summary>
    Url,

    Base64,

    /// <summary>The URL- and filename-safe alphabet of RFC 4648 §5, unpadded.</summary>
    Base64Url,
}

/// <summary>Whether a codec treats each line as its own text or the input as one text.</summary>
public enum CodecScope
{
    WholeText,
    EachLine,
}

/// <summary>
/// Runs a <see cref="TextCodec"/> over a settled source into a buffer writer. Line by line it
/// holds one line at a time; over the whole text it walks the source's contiguous runs carrying
/// at most a few bytes between them - either way the memory a run costs is its output.
///
/// Synchronous and blocking: a preview runs it on a few kilobytes, and applying it to a
/// document belongs on a background thread.
/// </summary>
public static class CodecRunner
{
    private const int CodecChunkBytes = 48 * 1024; // a multiple of 3 and of 4, for base64

    private static readonly SearchValues<byte> Base64Whitespace = SearchValues.Create(" \t\r\n"u8);

    /// <param name="direction">Forward encodes, reverse decodes.</param>
    /// <param name="inputIsPrefix">The input is the start of a longer text, as a preview's
    /// is: a base64 decode stops at the last whole quantum instead of refusing a cut one.</param>
    /// <exception cref="TransformException">The input cannot be decoded; the message is for the
    /// user.</exception>
    public static void Run(TextCodec codec, TransformDirection direction, CodecScope scope, IByteSource input,
        IBufferWriter<byte> output, CancellationToken stopping, bool inputIsPrefix = false)
    {
        if (direction == TransformDirection.Forward)
            Encode(codec, scope, input, output, stopping);
        else
            Decode(codec, scope, input, output, stopping, inputIsPrefix);
    }

    private static void Encode(TextCodec codec, CodecScope scope, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        if (scope == CodecScope.EachLine)
        {
            EachLine(input, output, stopping, (line, writer) => EncodeWhole(codec, line, writer));
            return;
        }

        switch (codec)
        {
            case TextCodec.JsonString:
                EachRun(input, stopping, run => TextEscaping.JsonEscape(run, output));
                break;
            case TextCodec.Url:
                EachRun(input, stopping, run => TextEscaping.UrlEncode(run, output));
                break;
            default:
                Base64EncodeStream(codec == TextCodec.Base64Url, input, output, stopping);
                break;
        }
    }

    private static void Decode(TextCodec codec, CodecScope scope, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping, bool inputIsPrefix)
    {
        // Encoded JSON and URL text has no raw line breaks of its own, so line by line is
        // whole-text too - any break in the input is layout, kept as it is.
        if (scope == CodecScope.WholeText && codec is TextCodec.Base64 or TextCodec.Base64Url)
        {
            Base64DecodeStream(codec == TextCodec.Base64Url, input, output, stopping, inputIsPrefix);
            return;
        }

        EachLine(input, output, stopping, (line, writer) => DecodeLine(codec, line, writer));
    }

    private static void EncodeWhole(TextCodec codec, ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        switch (codec)
        {
            case TextCodec.JsonString:
                TextEscaping.JsonEscape(text, output);
                break;
            case TextCodec.Url:
                TextEscaping.UrlEncode(text, output);
                break;
            default:
                Base64Encode(codec == TextCodec.Base64Url, text, output, isFinalBlock: true, out _);
                break;
        }
    }

    private static void DecodeLine(TextCodec codec, ReadOnlySpan<byte> line, IBufferWriter<byte> output)
    {
        switch (codec)
        {
            case TextCodec.JsonString:
                // Accept the string with its quotes too, as copied out of a document.
                var body = line.Trim(TransformLineReader.SpacesAndTabs);
                if (body.Length >= 2 && body[0] == (byte)'"' && body[^1] == (byte)'"')
                    body = body[1..^1];
                TextEscaping.JsonUnescape(body, output);
                break;
            case TextCodec.Url:
                TextEscaping.UrlDecode(line, output);
                break;
            default:
                var pending = new Base64Pending(codec == TextCodec.Base64Url, output);
                try
                {
                    pending.Add(line);
                    pending.Finish(inputIsPrefix: false);
                }
                finally
                {
                    pending.Dispose();
                }

                break;
        }
    }

    private delegate void LineTransform(ReadOnlySpan<byte> line, IBufferWriter<byte> output);

    /// <summary>Each line transformed on its own, with the input's line breaks kept.</summary>
    private static void EachLine(IByteSource input, IBufferWriter<byte> output, CancellationToken stopping, LineTransform transform)
    {
        using var reader = new TransformLineReader(input);
        bool first = true;
        while (reader.TryRead(out var line))
        {
            reader.ThrowIfStopping(stopping);
            if (!first)
                output.Write(reader.NewLine);

            first = false;
            try
            {
                transform(line, output);
            }
            catch (TransformException ex)
            {
                throw new TransformException($"Line {reader.LineNumber}: the text {ex.Message}.");
            }
        }

        if (reader.EndedWithNewline)
            output.Write(reader.NewLine);
    }

    private delegate void RunTransform(ReadOnlySpan<byte> run);

    /// <summary>Hands each contiguous run of a settled source to <paramref name="transform"/>.</summary>
    private static void EachRun(IByteSource input, CancellationToken stopping, RunTransform transform)
    {
        if (!input.LengthSettled)
            throw new InvalidOperationException("A transform runs over a document that has finished arriving.");

        long length = input.AvailableLength;
        long position = 0;
        while (position < length)
        {
            stopping.ThrowIfCancellationRequested();
            var run = input.GetContiguousSpan(position, (int)Math.Min(CodecChunkBytes, length - position));
            if (run.IsEmpty)
                throw new InvalidOperationException($"The document ended at byte {position:N0} of {length:N0}.");

            try
            {
                transform(run);
            }
            catch (TransformException ex)
            {
                throw new TransformException($"Near byte {position:N0}: the text {ex.Message}.");
            }

            position += run.Length;
        }
    }

    private static void Base64EncodeStream(bool urlSafe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        // Up to two bytes left over from a run that was not a multiple of three.
        byte[] carryArray = new byte[3];
        int carried = 0;
        EachRun(input, stopping, run =>
        {
            if (carried > 0)
            {
                int take = Math.Min(3 - carried, run.Length);
                run[..take].CopyTo(carryArray.AsSpan(carried));
                carried += take;
                run = run[take..];
                if (carried < 3)
                    return;

                Base64Encode(urlSafe, carryArray, output, isFinalBlock: false, out _);
                carried = 0;
            }

            Base64Encode(urlSafe, run, output, isFinalBlock: false, out int consumed);
            run[consumed..].CopyTo(carryArray);
            carried = run.Length - consumed;
        });

        Base64Encode(urlSafe, carryArray.AsSpan(0, carried), output, isFinalBlock: true, out _);
    }

    private static void Base64Encode(bool urlSafe, ReadOnlySpan<byte> bytes, IBufferWriter<byte> output, bool isFinalBlock, out int consumed)
    {
        consumed = 0;
        while (true)
        {
            var piece = bytes.Slice(consumed, Math.Min(CodecChunkBytes, bytes.Length - consumed));
            bool last = consumed + piece.Length == bytes.Length;
            var destination = output.GetSpan(Base64.GetMaxEncodedToUtf8Length(piece.Length) + 4);
            int read, written;
            if (urlSafe)
                Base64Url.EncodeToUtf8(piece, destination, out read, out written, isFinalBlock && last);
            else
                Base64.EncodeToUtf8(piece, destination, out read, out written, isFinalBlock && last);

            output.Advance(written);
            consumed += read;
            if (last || read < piece.Length)
                return;
        }
    }

    private static void Base64DecodeStream(bool urlSafe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping, bool inputIsPrefix)
    {
        var pending = new Base64Pending(urlSafe, output);
        try
        {
            EachRun(input, stopping, run => pending.Add(run));
            pending.Finish(inputIsPrefix);
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>
    /// Base64 text with its whitespace removed, decoded a buffer at a time - whole quanta of four
    /// while more may come, everything at the end. Lenient about a missing trailing
    /// <c>=</c>, which copied base64 often loses.
    /// </summary>
    private sealed class Base64Pending(bool urlSafe, IBufferWriter<byte> output) : IDisposable
    {
        private byte[] text = ArrayPool<byte>.Shared.Rent(CodecChunkBytes);
        private int count;
        private long decodedChars;

        public void Add(ReadOnlySpan<byte> run)
        {
            while (!run.IsEmpty)
            {
                int space = run.IndexOfAny(Base64Whitespace);
                var characters = space < 0 ? run : run[..space];
                while (!characters.IsEmpty)
                {
                    int take = Math.Min(this.text.Length - this.count, characters.Length);
                    characters[..take].CopyTo(this.text.AsSpan(this.count));
                    this.count += take;
                    characters = characters[take..];
                    if (this.count == this.text.Length)
                        Flush(final: false);
                }

                run = space < 0 ? default : run[(space + 1)..];
            }
        }

        public void Finish(bool inputIsPrefix)
        {
            if (inputIsPrefix)
                this.count -= this.count % 4;
            else if (!urlSafe && this.count % 4 is 2 or 3)
                Pad();

            Flush(final: true);
        }

        private void Pad()
        {
            while (this.count % 4 != 0)
                this.text[this.count++] = (byte)'=';
        }

        private void Flush(bool final)
        {
            int take = final ? this.count : this.count - this.count % 4;
            var source = this.text.AsSpan(0, take);
            var destination = output.GetSpan(Base64.GetMaxDecodedFromUtf8Length(take) + 3);
            var status = urlSafe
                ? Base64Url.DecodeFromUtf8(source, destination, out int read, out int written, final)
                : Base64.DecodeFromUtf8(source, destination, out read, out written, final);

            if (status != OperationStatus.Done && !(status == OperationStatus.NeedMoreData && !final))
                throw new TransformException($"is not valid {(urlSafe ? "URL-safe base64" : "base64")} (near character {this.decodedChars + read:N0})");

            output.Advance(written);
            this.decodedChars += read;
            this.text.AsSpan(read, this.count - read).CopyTo(this.text);
            this.count -= read;
        }

        public void Dispose()
        {
            if (this.text.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(this.text);
                this.text = [];
            }
        }
    }
}
