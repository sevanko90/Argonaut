using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Recipes;

/// <summary>
/// Runs a <see cref="Recipe"/> over a settled source into a buffer writer. Everything streams:
/// line-shaped work holds one line at a time (see <see cref="RecipeLineReader"/>), and
/// whole-text codecs walk the source's contiguous runs carrying at most a few bytes between
/// them, so the memory a run costs is its output and nothing proportional to its input.
///
/// Synchronous and blocking: a preview runs it on a few kilobytes, and applying it to a
/// document belongs on a background thread.
/// </summary>
public static class RecipeRunner
{
    private const int CodecChunkBytes = 48 * 1024; // a multiple of 3 and of 4, for base64

    private static readonly SearchValues<byte> Base64Whitespace = SearchValues.Create(" \t\r\n"u8);

    /// <param name="inputIsPrefix">The input is the start of a longer text, as a preview's
    /// is: a base64 decode stops at the last whole quantum instead of refusing a cut one.</param>
    /// <exception cref="RecipeException">The input does not fit the recipe; the message is for
    /// the user.</exception>
    public static void Run(Recipe recipe, RecipeDirection direction, IByteSource input, IBufferWriter<byte> output,
        CancellationToken stopping, bool inputIsPrefix = false)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        switch (recipe.Kind, direction)
        {
            case (RecipeKind.Template, RecipeDirection.Forward):
                Wrap(recipe, input, output, stopping);
                break;
            case (RecipeKind.Template, RecipeDirection.Reverse):
                Unwrap(recipe, input, output, stopping);
                break;
            case (RecipeKind.Codec, RecipeDirection.Forward):
                Encode(recipe, input, output, stopping);
                break;
            default:
                Decode(recipe, input, output, stopping, inputIsPrefix);
                break;
        }
    }

    private static void Wrap(Recipe recipe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        var literals = SplitTemplate(recipe.Template);
        byte[] separator = Encoding.UTF8.GetBytes(recipe.Separator);

        using var reader = new RecipeLineReader(input);
        output.Write(Encoding.UTF8.GetBytes(recipe.Open));
        bool first = true;
        while (reader.TryRead(out var line))
        {
            ThrowEveryThousandLines(reader, stopping);
            if (recipe.TrimLines)
                line = line.Trim(Whitespace8);

            if (recipe.SkipBlankLines && line.IsEmpty)
                continue;

            if (!first)
                output.Write(separator);

            first = false;
            for (int i = 0; i < literals.Count; i++)
            {
                if (i > 0)
                    TextEscaping.Escape(recipe.Escape, line, output);

                output.Write(literals[i]);
            }
        }

        output.Write(Encoding.UTF8.GetBytes(recipe.Close));
    }

    /// <summary>
    /// Takes apart what <see cref="Wrap"/> produced: each item becomes one output line. Works a
    /// line at a time, so several items on one line (<c>('a','b')</c>) and one item per line both
    /// read the same way, and whitespace round the template's literals, separator and wrapper is
    /// tolerated - which is what lets it read a list typed by hand, not only one this made.
    /// </summary>
    private static void Unwrap(Recipe recipe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        var literals = SplitTemplate(recipe.Template);
        if (literals.Count != 2)
            throw new RecipeException($"To reverse it, the template needs exactly one {Recipe.LinePlaceholder}.");

        var shape = new ItemShape(
            literals[0].AsSpan().TrimStart(Whitespace8).ToArray(),
            literals[1].AsSpan().TrimEnd(Whitespace8).ToArray(),
            Encoding.UTF8.GetBytes(recipe.Separator.Trim()),
            recipe.Escape);
        byte[] open = Encoding.UTF8.GetBytes(recipe.Open.Trim());
        byte[] close = Encoding.UTF8.GetBytes(recipe.Close.Trim());

        using var reader = new RecipeLineReader(input);
        var items = new List<Range>();
        bool openPending = open.Length > 0;
        bool first = true;
        while (reader.TryRead(out var line))
        {
            ThrowEveryThousandLines(reader, stopping);
            line = line.Trim(" \t"u8);
            if (line.IsEmpty)
                continue;

            if (openPending)
            {
                openPending = false;
                if (line.StartsWith(open))
                    line = line[open.Length..].TrimStart(Whitespace8);
            }

            items.Clear();
            bool parsed = false;
            if (close.Length > 0 && line.EndsWith(close))
            {
                var inside = line[..^close.Length].TrimEnd(Whitespace8);
                parsed = shape.TryParse(inside, items, out _);
            }

            if (!parsed)
            {
                items.Clear();
                if (!shape.TryParse(line, items, out string? problem))
                    throw new RecipeException($"Line {reader.LineNumber}: {problem}.");
            }

            foreach (var item in items)
            {
                if (!first)
                    output.Write(reader.NewLine);

                first = false;
                var content = line[item];
                try
                {
                    TextEscaping.Unescape(recipe.Escape, content, output);
                }
                catch (RecipeException ex)
                {
                    throw new RecipeException($"Line {reader.LineNumber}: the item {ex.Message}.");
                }
            }
        }

        if (!first && reader.EndedWithNewline)
            output.Write(reader.NewLine);
    }

    /// <summary>One item's literals, as the reversed template looks for them: the text before
    /// the placeholder without its indentation, the text after it, and the separator trimmed.</summary>
    private readonly record struct ItemShape(byte[] Before, byte[] After, byte[] Separator, TextEscape Escape)
    {
        /// <summary>The items on one line, as ranges of their escaped contents.</summary>
        public bool TryParse(ReadOnlySpan<byte> line, List<Range> items, out string? problem)
        {
            int at = 0;
            while (true)
            {
                at = SkipWhitespace(line, at);
                if (at == line.Length)
                {
                    problem = null;
                    return true;
                }

                if (!line[at..].StartsWith(Before))
                {
                    problem = $"expected '{Encoding.UTF8.GetString(Before)}'";
                    return false;
                }

                at += Before.Length;
                int start = at;
                int end;
                if (After.Length > 0)
                {
                    end = FindAfter(line, at);
                    if (end < 0)
                    {
                        problem = $"no closing '{Encoding.UTF8.GetString(After)}'";
                        return false;
                    }

                    at = end + After.Length;
                }
                else
                {
                    int separator = Separator.Length > 0 ? line[at..].IndexOf(Separator) : -1;
                    end = separator < 0 ? line.Length : at + separator;
                    at = end;
                }

                // A bare template ("{line}") has nothing to say where the item stops but the
                // separator, so the spaces round it belong to the layout, not the item.
                if (Before.Length == 0 && After.Length == 0)
                {
                    while (start < end && IsWhitespace(line[start]))
                        start++;
                    while (end > start && IsWhitespace(line[end - 1]))
                        end--;
                }

                items.Add(start..end);

                at = SkipWhitespace(line, at);
                if (at < line.Length && Separator.Length > 0)
                {
                    if (!line[at..].StartsWith(Separator))
                    {
                        problem = $"expected '{Encoding.UTF8.GetString(Separator)}' after an item";
                        return false;
                    }

                    at += Separator.Length;
                }
            }
        }

        /// <summary>Where <see cref="After"/> next occurs outside an escape, or -1.</summary>
        private int FindAfter(ReadOnlySpan<byte> line, int from)
        {
            byte doubled = Escape switch
            {
                TextEscape.Sql => (byte)'\'',
                TextEscape.Csv => (byte)'"',
                _ => 0,
            };

            for (int i = from; i < line.Length; i++)
            {
                if (Escape == TextEscape.Json && line[i] == (byte)'\\')
                {
                    i++;
                    continue;
                }

                if (doubled != 0 && line[i] == doubled && i + 1 < line.Length && line[i + 1] == doubled)
                {
                    i++;
                    continue;
                }

                if (line[i..].StartsWith(After))
                    return i;
            }

            return -1;
        }
    }

    private static void Encode(Recipe recipe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        if (recipe.Scope == RecipeScope.EachLine)
        {
            EachLine(input, output, stopping, (line, writer) => EncodeWhole(recipe.Codec, line, writer));
            return;
        }

        switch (recipe.Codec)
        {
            case RecipeCodec.JsonString:
                EachRun(input, stopping, run => TextEscaping.JsonEscape(run, output));
                break;
            case RecipeCodec.Url:
                EachRun(input, stopping, run => TextEscaping.UrlEncode(run, output));
                break;
            default:
                Base64EncodeStream(recipe.Codec == RecipeCodec.Base64Url, input, output, stopping);
                break;
        }
    }

    private static void Decode(Recipe recipe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping, bool inputIsPrefix)
    {
        // Encoded JSON and URL text has no raw line breaks of its own, so line by line is
        // whole-text too - any break in the input is layout, kept as it is.
        if (recipe.Scope == RecipeScope.WholeText && recipe.Codec is RecipeCodec.Base64 or RecipeCodec.Base64Url)
        {
            Base64DecodeStream(recipe.Codec == RecipeCodec.Base64Url, input, output, stopping, inputIsPrefix);
            return;
        }

        EachLine(input, output, stopping, (line, writer) => DecodeLine(recipe.Codec, line, writer));
    }

    private static void EncodeWhole(RecipeCodec codec, ReadOnlySpan<byte> text, IBufferWriter<byte> output)
    {
        switch (codec)
        {
            case RecipeCodec.JsonString:
                TextEscaping.JsonEscape(text, output);
                break;
            case RecipeCodec.Url:
                TextEscaping.UrlEncode(text, output);
                break;
            default:
                Base64Encode(codec == RecipeCodec.Base64Url, text, output, isFinalBlock: true, out _);
                break;
        }
    }

    private static void DecodeLine(RecipeCodec codec, ReadOnlySpan<byte> line, IBufferWriter<byte> output)
    {
        switch (codec)
        {
            case RecipeCodec.JsonString:
                // Accept the string with its quotes too, as copied out of a document.
                var body = line.Trim(Whitespace8);
                if (body.Length >= 2 && body[0] == (byte)'"' && body[^1] == (byte)'"')
                    body = body[1..^1];
                TextEscaping.JsonUnescape(body, output);
                break;
            case RecipeCodec.Url:
                TextEscaping.UrlDecode(line, output);
                break;
            default:
                var pending = new Base64Pending(codec == RecipeCodec.Base64Url, output);
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
        using var reader = new RecipeLineReader(input);
        bool first = true;
        while (reader.TryRead(out var line))
        {
            ThrowEveryThousandLines(reader, stopping);
            if (!first)
                output.Write(reader.NewLine);

            first = false;
            try
            {
                transform(line, output);
            }
            catch (RecipeException ex)
            {
                throw new RecipeException($"Line {reader.LineNumber}: the text {ex.Message}.");
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
            throw new InvalidOperationException("A recipe runs over a document that has finished arriving.");

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
            catch (RecipeException ex)
            {
                throw new RecipeException($"Near byte {position:N0}: the text {ex.Message}.");
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
                throw new RecipeException($"is not valid {(urlSafe ? "URL-safe base64" : "base64")} (near character {this.decodedChars + read:N0})");

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

    /// <summary>The template's literal text either side of each placeholder, as UTF-8 - one
    /// more literal than there are placeholders.</summary>
    private static List<byte[]> SplitTemplate(string template)
    {
        var literals = new List<byte[]>();
        int from = 0;
        int at;
        while ((at = template.IndexOf(Recipe.LinePlaceholder, from, StringComparison.Ordinal)) >= 0)
        {
            literals.Add(Encoding.UTF8.GetBytes(template[from..at]));
            from = at + Recipe.LinePlaceholder.Length;
        }

        literals.Add(Encoding.UTF8.GetBytes(template[from..]));
        return literals;
    }

    private static void ThrowEveryThousandLines(RecipeLineReader reader, CancellationToken stopping)
    {
        if (reader.LineNumber % 1024 == 0)
            stopping.ThrowIfCancellationRequested();
    }

    private static ReadOnlySpan<byte> Whitespace8 => " \t"u8;

    private static int SkipWhitespace(ReadOnlySpan<byte> line, int at)
    {
        while (at < line.Length && IsWhitespace(line[at]))
            at++;
        return at;
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t';
}
