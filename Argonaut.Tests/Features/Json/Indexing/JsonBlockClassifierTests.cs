using System.Text;
using System.Text.Json;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Indexing;

public class JsonBlockClassifierTests
{
    public static TheoryData<int> Seeds => new() { 1, 2, 3, 4, 5 };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void VectorAndScalarPathsAgreeOnEveryBlockWithoutComments(int seed)
    {
        byte[] json = RandomJson.Document(new Random(seed), elements: 300);
        var vector = default(JsonBlockClassifier);
        var scalar = JsonBlockClassifier.CommentAware();

        foreach (var block in Blocks(json))
        {
            vector.Classify(block, out var fromVector);
            scalar.Classify(block, out var fromScalar);

            Assert.Equal(fromScalar, fromVector);
        }

        Assert.False(vector.IsCommentAware);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void BracketsAreExactlyTheReadersContainerTokens(int seed, bool jsonc)
    {
        byte[] json = RandomJson.Document(new Random(seed), elements: 300, jsonc);
        var classifier = default(JsonBlockClassifier);
        var brackets = new List<long>();
        long blockOffset = 0;

        foreach (var block in Blocks(json))
        {
            classifier.Classify(block, out var masks);
            for (ulong bits = masks.Open | masks.Close; bits != 0; bits &= bits - 1)
                brackets.Add(blockOffset + System.Numerics.BitOperations.TrailingZeroCount(bits));
            blockOffset += JsonBlockClassifier.BlockSize;
        }

        Assert.Equal(ContainerTokenOffsets(json), brackets);
        Assert.Equal(jsonc, classifier.IsCommentAware);
    }

    [Fact]
    public void CommentsSplitAcrossBlocksAreStillComments()
    {
        // Slide a comment's opener, body and closer across every alignment of a block boundary.
        foreach (string comment in new[] { "/* ] */", "// ]\n", "/*/ ] */", "/* **/" })
        {
            for (int pad = 0; pad < 70; pad++)
            {
                byte[] json = Encoding.UTF8.GetBytes(new string(' ', pad) + "[1," + comment + "2]");
                var classifier = default(JsonBlockClassifier);
                int closes = 0;
                foreach (var block in Blocks(json))
                {
                    classifier.Classify(block, out var masks);
                    closes += System.Numerics.BitOperations.PopCount(masks.Close);
                }

                Assert.Equal(1, closes);
            }
        }
    }

    [Fact]
    public void Escaped_OddRunsEscapeTheNextByteAndCarryAcrossTheBlock()
    {
        bool escapesNext = false;

        // Backslashes at 0, 2-3 and 63: 0 escapes 1, 2 escapes the backslash at 3 (so 3 escapes
        // nothing, and byte 4 is not escaped), and 63 carries into the next block.
        ulong escaped = JsonBlockClassifier.Escaped(0b1101UL | (1UL << 63), ref escapesNext);

        Assert.Equal(0b1010UL, escaped);
        Assert.True(escapesNext);

        // The carried escape consumes this block's leading backslash, so it escapes nothing.
        escaped = JsonBlockClassifier.Escaped(0b1UL, ref escapesNext);

        Assert.Equal(0b1UL, escaped);
        Assert.False(escapesNext);
    }

    /// <summary>The document in 64-byte blocks, the last padded with spaces.</summary>
    private static IEnumerable<byte[]> Blocks(byte[] json)
    {
        for (int offset = 0; offset < json.Length; offset += JsonBlockClassifier.BlockSize)
        {
            var block = new byte[JsonBlockClassifier.BlockSize];
            Array.Fill(block, (byte)' ');
            int length = Math.Min(block.Length, json.Length - offset);
            Array.Copy(json, offset, block, 0, length);
            yield return block;
        }
    }

    private static List<long> ContainerTokenOffsets(byte[] json)
    {
        var offsets = new List<long>();
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.EndObject or JsonTokenType.EndArray)
                offsets.Add(reader.TokenStartIndex);
        }

        return offsets;
    }
}
