using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Where a parse failure says the trouble is. This offset is what the failure banner's link and
/// the incompatible-file placeholder jump to, so "roughly there" costs the user a hunt at exactly
/// the moment they are least inclined to do one.
///
/// The end of the last successfully indexed token is not the answer. Between it and the content
/// that actually broke sits the punctuation joining the two - a comma before the next element, a
/// colon before a value - plus the newline and indentation after it, which lands a reader on the
/// tail of the previous line.
/// </summary>
public class JsonFailureLocationTests
{
    private static async Task<IndexFailure> FailureFor(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));

        MMapFile? file = null;
        try
        {
            file = new MMapFile(path);
            var index = JsonStructureIndex.StartIndexing(file);
            try
            {
                await index.IndexingTask;
            }
            catch
            {
                // The scan is expected to fault; the failure it recorded is the subject here.
            }

            Assert.NotNull(index.Failure);
            return index.Failure!;
        }
        finally
        {
            file?.Dispose();
            File.Delete(path);
        }
    }

    /// <summary>
    /// The reported case: a multi-line array of objects with a broken element. The offset must be
    /// the first character of the offending line, not the end of the line before it.
    /// </summary>
    [Fact]
    public async Task PointsAtTheStartOfTheOffendingLine()
    {
        var json = new StringBuilder();
        json.Append("[\n");
        json.Append("  {\"id\":1},\n");
        json.Append("  {\"id\":2},\n");
        json.Append("  {\"id\":oops}\n");
        json.Append("]\n");

        string text = json.ToString();
        var failure = await FailureFor(text);

        Assert.NotNull(failure.ByteOffset);

        // The offending line is the one holding "oops"; the offset must land on its first
        // non-whitespace character, the opening brace.
        int expected = text.IndexOf("{\"id\":oops}", StringComparison.Ordinal);
        Assert.Equal(expected, failure.ByteOffset!.Value);
    }

    /// <summary>
    /// A bad value after a property name lands on the start of the line holding it, not on the
    /// value itself: the line is the unit a reader is looking for, and the property name is the
    /// thing that identifies which one broke.
    /// </summary>
    [Fact]
    public async Task PointsAtTheLineHoldingABadValue()
    {
        const string text = "{\n  \"a\": 1,\n  \"b\": oops\n}\n";

        var failure = await FailureFor(text);

        Assert.Equal(text.IndexOf("\"b\"", StringComparison.Ordinal), failure.ByteOffset!.Value);
    }

    /// <summary>
    /// Minified JSON is one line as long as the file, so snapping to its start would send a
    /// reader to byte zero of a multi-GB document instead of to the problem. With no line break
    /// nearby, the precise position is kept.
    /// </summary>
    [Fact]
    public async Task OnASingleLongLine_KeepsThePrecisePositionRatherThanTheLineStart()
    {
        string filler = string.Join(',', Enumerable.Range(0, 2000).Select(i => $"{{\"id\":{i}}}"));
        string text = $"[{filler},{{\"id\":oops}}]";

        var failure = await FailureFor(text);

        // Nowhere near the start of the document, despite the whole thing being one line.
        Assert.True(failure.ByteOffset!.Value > text.Length - 200,
            $"landed at {failure.ByteOffset.Value} in a {text.Length}-byte single-line document");
    }

    /// <summary>
    /// Nothing valid was read at all, so there is no last good token to reason from and the
    /// document's start is the only honest answer.
    /// </summary>
    [Fact]
    public async Task ReportsTheStartWhenNothingParsed()
    {
        var failure = await FailureFor("oops");

        Assert.Equal(0, failure.ByteOffset!.Value);
    }

    /// <summary>
    /// The line and column still come from the reader, and are still reported - the derived byte
    /// offset replaces neither.
    /// </summary>
    [Fact]
    public async Task StillReportsLineAndColumn()
    {
        var failure = await FailureFor("[\n  1,\n  oops\n]\n");

        Assert.NotNull(failure.Line);
        Assert.NotNull(failure.Column);
        Assert.True(failure.ItemsIndexed > 0);
    }
}
