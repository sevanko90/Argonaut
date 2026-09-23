using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests;

/// <summary>
/// <see cref="RawLineRows"/> is the claim that a line's rows are arithmetic from where it starts
/// and ends. <see cref="RawEditedRowIndex"/> trusts it for every row inside a dirty span without
/// ever walking one, so if it drifts from a real <see cref="RawRowCursor"/> walk by a byte the
/// caret lands on a different row than the one drawn - silently. The oracle is therefore the walk
/// itself, over content built to put every break rule on or next to a cap: multi-byte characters
/// of every width, CRLF, a '\n' exactly on a cap, runs of invalid continuation bytes, empty lines,
/// and an unterminated tail.
/// </summary>
public class RawLineRowsTests
{
    private readonly record struct WalkedRow(long Start, long End, bool SoftWrap);

    private static byte[] RandomContent(Random random, int wrapWidth, bool terminated)
    {
        var bytes = new List<byte>();
        int lines = random.Next(5, 40);
        for (int line = 0; line < lines; line++)
        {
            int kind = random.Next(8);
            if (kind == 0)
            {
                // Content ending exactly on a cap, so the '\n' sits at lineStart + k·W and is
                // peek-extended into the row before it.
                bytes.AddRange(Enumerable.Repeat((byte)'x', wrapWidth * random.Next(1, 4)));
            }
            else if (kind == 1)
            {
                // Pure continuation bytes: invalid UTF-8, breaks at the cap regardless.
                bytes.AddRange(Enumerable.Repeat((byte)0x80, random.Next(0, wrapWidth * 5)));
            }
            else if (kind != 2) // kind 2 is an empty line
            {
                int pieces = random.Next(0, wrapWidth);
                for (int i = 0; i < pieces; i++)
                {
                    string piece = random.Next(6) switch
                    {
                        0 => "é",
                        1 => "日",
                        2 => "😀",
                        3 => "ab",
                        _ => "x",
                    };
                    bytes.AddRange(Encoding.UTF8.GetBytes(piece));
                }
            }

            if (random.Next(6) == 0)
                bytes.Add((byte)'\r');

            bytes.Add((byte)'\n');
        }

        if (!terminated)
            bytes.AddRange(Encoding.UTF8.GetBytes(new string('t', random.Next(1, wrapWidth * 3)) + "é"));

        return bytes.ToArray();
    }

    /// <summary>Every line of <paramref name="source"/>, as the rows a cursor walk gives it.</summary>
    private static List<List<WalkedRow>> WalkLines(IByteSource source, int wrapWidth)
    {
        var lines = new List<List<WalkedRow>>();
        var cursor = RawRowCursor.StartOfLine(0, 1, wrapWidth);
        List<WalkedRow>? current = null;
        while (cursor.Start < source.AvailableLength)
        {
            if (cursor.AtLineStart)
                lines.Add(current = new List<WalkedRow>());

            long start = cursor.Start;
            var (end, softWrap) = cursor.Advance(source, wrapWidth);
            current!.Add(new WalkedRow(start, end, softWrap));
        }

        return lines;
    }

    private static void AssertGeometryMatchesTheWalk(byte[] content, int wrapWidth, string because)
    {
        var source = new MemoryByteSource(content);
        foreach (var rows in WalkLines(source, wrapWidth))
        {
            long lineStart = rows[0].Start;
            long lineEnd = rows[^1].End;
            bool terminated = content[lineEnd - 1] == (byte)'\n';
            var geometry = new RawLineRows(lineStart, terminated ? lineEnd - 1 : lineEnd, terminated, wrapWidth);

            Assert.True(rows.Count == geometry.Count, $"{because}: line at {lineStart} walks to {rows.Count} rows, geometry says {geometry.Count}");
            for (int k = 0; k < rows.Count; k++)
            {
                var (start, end, softWrap) = geometry.Range(source, k);
                Assert.True(new WalkedRow(start, end, softWrap) == rows[k],
                    $"{because}: line at {lineStart} row {k} walks to {rows[k]}, geometry says ({start}, {end}, {softWrap})");

                for (long offset = rows[k].Start; offset < rows[k].End; offset++)
                    Assert.True(geometry.RowContaining(source, offset) == k,
                        $"{because}: offset {offset} is in row {k}, geometry says {geometry.RowContaining(source, offset)}");
            }
        }

        // The phantom line after a trailing newline occupies no rows.
        if (content.Length == 0 || content[^1] == (byte)'\n')
            Assert.Equal(0, new RawLineRows(content.Length, content.Length, false, wrapWidth).Count);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(40)]
    [InlineData(80)]
    public void RandomContent_GeometryAgreesWithACursorWalk(int wrapWidth)
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var random = new Random(seed * 31 + wrapWidth);
            bool terminated = seed % 2 == 0;
            AssertGeometryMatchesTheWalk(RandomContent(random, wrapWidth, terminated), wrapWidth, $"W={wrapWidth} seed {seed}");
        }
    }

    [Fact]
    public void EmptyTerminatedLine_IsOneRow()
    {
        Assert.Equal(1, new RawLineRows(5, 5, true, 16).Count);
        AssertGeometryMatchesTheWalk("abc\n\n\nd"u8.ToArray(), 16, "empty lines");
    }

    [Fact]
    public void AnInsertLeavesLaterBoundariesWithinThreeBytes()
    {
        // The property the whole design rests on, stated over the geometry directly: each boundary
        // depends only on the bytes at its own cap, so shifting content moves it by at most the
        // backoff range and never chains.
        var text = new StringBuilder();
        while (text.Length < 4000)
            text.Append("abcdéfghij日");
        byte[] before = Encoding.UTF8.GetBytes(text.ToString());
        byte[] after = [.. before.Take(100), (byte)'Z', .. before.Skip(100)];

        var original = new RawLineRows(0, before.Length, false, 80);
        var edited = new RawLineRows(0, after.Length, false, 80);
        var beforeSource = new MemoryByteSource(before);
        var afterSource = new MemoryByteSource(after);
        for (int k = 2; k < original.Count; k++)
            Assert.InRange(edited.Start(afterSource, k) - original.Start(beforeSource, k), -3, 3);
    }
}
