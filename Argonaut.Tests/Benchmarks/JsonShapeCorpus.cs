using System.Text.Json;

namespace Argonaut.Tests.Benchmarks;

/// <summary>The document shapes the JSON tree benchmarks compare, chosen for how differently
/// they load a per-token index.</summary>
public enum JsonShape
{
    /// <summary>One flat array of short numbers: 2-4 bytes a token, the worst case for any index
    /// that keeps a record per token.</summary>
    TokenDenseArray,

    /// <summary>An array of chains 16 objects deep, each level a few short members: many small
    /// containers and a deep ancestor stack.</summary>
    DeepNesting,

    /// <summary>An array of flat-ish records with a nested object and a short array each: the
    /// common shape of a large export.</summary>
    RecordArray,
}

/// <summary>
/// Writes a compact, deterministic document of a given shape at exactly a given size, so a
/// benchmark can divide by the size it asked for. Elements are written until the next one might
/// not fit, the root is closed, and the remainder is padded with trailing whitespace - valid
/// after a root value, and never tokenised.
/// </summary>
public static class JsonShapeCorpus
{
    public const int NestingDepth = 16;

    // Comfortably larger than any one element of any shape, so the root's closing bracket
    // always fits inside the requested size.
    private const int ElementHeadroom = 16 * 1024;

    public static void Write(string path, JsonShape shape, long sizeBytes)
    {
        using (var stream = File.Create(path))
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
            {
                writer.WriteStartArray();
                for (int element = 0; writer.BytesCommitted + writer.BytesPending < sizeBytes - ElementHeadroom; element++)
                {
                    WriteElement(writer, shape, element);
                    if (writer.BytesPending > 64 * 1024)
                        writer.Flush();
                }

                writer.WriteEndArray();
                writer.Flush();
            }

            long padding = sizeBytes - stream.Length;
            if (padding < 0)
                throw new InvalidOperationException($"{shape} overran {sizeBytes} bytes by {-padding}.");

            var spaces = new byte[64 * 1024];
            spaces.AsSpan().Fill((byte)' ');
            while (padding > 0)
            {
                int chunk = (int)Math.Min(padding, spaces.Length);
                stream.Write(spaces, 0, chunk);
                padding -= chunk;
            }
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonShape shape, int element)
    {
        switch (shape)
        {
            case JsonShape.TokenDenseArray:
                writer.WriteNumberValue(element % 1000);
                break;

            case JsonShape.DeepNesting:
                WriteChain(writer, element, NestingDepth);
                break;

            case JsonShape.RecordArray:
                WriteRecord(writer, element);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static void WriteChain(Utf8JsonWriter writer, int element, int levels)
    {
        for (int level = 0; level < levels; level++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", element + level);
            writer.WriteString("n", "x");
            writer.WritePropertyName("c");
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(1);
        writer.WriteNumberValue(2);
        writer.WriteNumberValue(3);
        writer.WriteEndArray();

        for (int level = 0; level < levels; level++)
            writer.WriteEndObject();
    }

    private static void WriteRecord(Utf8JsonWriter writer, int element)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", element);
        writer.WriteString("name", "user-" + element);
        writer.WriteBoolean("active", element % 3 != 0);
        writer.WriteNumber("score", element % 1000 / 10.0);
        writer.WriteStartArray("tags");
        writer.WriteStringValue("red");
        writer.WriteStringValue("blue");
        writer.WriteEndArray();
        writer.WriteStartObject("address");
        writer.WriteString("city", "Leeds");
        writer.WriteString("zip", "LS1");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
