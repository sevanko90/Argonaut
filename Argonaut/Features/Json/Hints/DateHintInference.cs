using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Infers the file-level default date scheme from the first classified Number token in
/// document order, scanning at most maxTokens already-indexed tokens - never a full-file scan.
/// Safe to run on a background thread: JsonStructureIndex reads and MMapFile spans are
/// read-only.
/// </summary>
public static class DateHintInference
{
    public const int MaxTokensToScan = 5000;

    public static DateDecodingScheme? FindFirstScheme(JsonStructureIndex index, MMapFile mmap, int maxTokens)
    {
        int limit = System.Math.Min(index.TokenCount, maxTokens);

        for (int i = 0; i < limit; i++)
        {
            var token = index.GetToken(i);
            if (token.Kind != JsonTokenKind.Number)
                continue;

            var raw = mmap.GetSpan(token.Offset, token.Length);
            if (DateHintClassifier.TryClassify(raw, out _, out var scheme))
                return scheme;
        }

        return null;
    }
}
