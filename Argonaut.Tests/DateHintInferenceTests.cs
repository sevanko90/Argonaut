using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

public class DateHintInferenceTests
{
    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    [Fact]
    public void FindsFirstClassifiedNumber_InDocumentOrder()
    {
        // "b":123 is a Number too short to classify; "c" is the first classifiable one.
        const string json = "{\"a\":\"x\",\"b\":123,\"c\":1709305509,\"d\":1709305509000}";
        var (index, mmap, path) = BuildIndex(json);
        try
        {
            var scheme = DateHintInference.FindFirstScheme(index, mmap, DateHintInference.MaxTokensToScan);
            Assert.Equal(DateDecodingScheme.JsSeconds, scheme);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void NoCandidates_ReturnsNull()
    {
        const string json = "{\"a\":\"x\",\"b\":123,\"c\":true}";
        var (index, mmap, path) = BuildIndex(json);
        try
        {
            var scheme = DateHintInference.FindFirstScheme(index, mmap, DateHintInference.MaxTokensToScan);
            Assert.Null(scheme);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void MaxTokensCap_IsRespected()
    {
        // The classifiable value sits well past a small cap, so it must not be found.
        const string json = "{\"a\":1,\"b\":2,\"c\":3,\"d\":1709305509}";
        var (index, mmap, path) = BuildIndex(json);
        try
        {
            var scheme = DateHintInference.FindFirstScheme(index, mmap, maxTokens: 4);
            Assert.Null(scheme);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
