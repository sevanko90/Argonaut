using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Infers the file-level default date scheme from the first classified Number token in
/// document order, scanning at most maxTokens already-indexed tokens - never a full-file scan.
/// Safe to run on a background thread: JsonStructureIndex reads and IByteSource spans are
/// read-only.
/// </summary>
public static class DateHintInference
{
    public const int MaxTokensToScan = 5000;

    /// <summary><see cref="FindFirstScheme(JsonStructureIndex, IByteSource, int)"/> for the
    /// sparse tree: the first <paramref name="maxValues"/> values in document order, read as if
    /// every container were open.</summary>
    public static DateDecodingScheme? FindFirstScheme(SparseContainerIndex index, JsonTreeReader reader, JsonTreeText text, int maxValues)
    {
        var cursor = new TreeCursor(index, reader, new TreeExpandState(int.MaxValue));
        int seen = 0;
        for (bool more = cursor.MoveToStart(); more && seen < maxValues; more = cursor.MoveNext(), seen++)
        {
            var row = cursor.Current;
            if (row.Shape != TreeRowShape.Leaf || row.Node.FormatKind != (byte)JsonTokenKind.Number)
                continue;

            var raw = text.ScalarBytes(row, 64);
            if (!raw.IsEmpty && DateHintClassifier.TryClassify(raw, out _, out var scheme))
                return scheme;
        }

        return null;
    }

    public static DateDecodingScheme? FindFirstScheme(JsonStructureIndex index, IByteSource bytes, int maxTokens)
    {
        int limit = System.Math.Min(index.TokenCount, maxTokens);

        for (int i = 0; i < limit; i++)
        {
            var token = index.GetToken(i);
            if (token.Kind != JsonTokenKind.Number)
                continue;

            var raw = bytes.RequireContiguous(token.Offset, token.Length);
            if (DateHintClassifier.TryClassify(raw, out _, out var scheme))
                return scheme;
        }

        return null;
    }
}
