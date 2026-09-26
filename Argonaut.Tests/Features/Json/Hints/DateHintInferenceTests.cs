using Argonaut.Features.Json.Hints;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Hints;

/// <summary>The scheme a document's first date-like number suggests, looking only so far.</summary>
public class DateHintInferenceTests
{
    private static DateDecodingScheme? FirstScheme(string json, int maxValues = DateHintInference.MaxValuesToScan)
    {
        var tree = new JsonTreeHarness(json);
        return DateHintInference.FindFirstScheme(tree.Index.Structure, tree.Reader, tree.Text, maxValues);
    }

    [Fact]
    public void FindsFirstClassifiedNumber_InDocumentOrder()
    {
        // "b":123 is a Number too short to classify; "c" is the first classifiable one.
        Assert.Equal(DateDecodingScheme.JsSeconds, FirstScheme("{\"a\":\"x\",\"b\":123,\"c\":1709305509,\"d\":1709305509000}"));
    }

    [Fact]
    public void NoCandidates_ReturnsNull()
        => Assert.Null(FirstScheme("{\"a\":\"x\",\"b\":123,\"c\":true}"));

    [Fact]
    public void TheValueCapIsRespected()
    {
        // The classifiable value is the fifth row (the root, then a, b, c), past a cap of four.
        const string json = "{\"a\":1,\"b\":2,\"c\":3,\"d\":1709305509}";
        Assert.Null(FirstScheme(json, maxValues: 4));
        Assert.Equal(DateDecodingScheme.JsSeconds, FirstScheme(json, maxValues: 5));
    }
}
