using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Infers the file-level default date scheme from the first classified number in document order,
/// reading at most a fixed number of rows - never a full-file scan. Safe to run on a background
/// thread with its own reader and text.
/// </summary>
public static class DateHintInference
{
    public const int MaxValuesToScan = 5000;

    /// <summary>The scheme of the first number that classifies as a date among the first
    /// <paramref name="maxValues"/> rows in document order, read as if every container were
    /// open; null when none does.</summary>
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
}
