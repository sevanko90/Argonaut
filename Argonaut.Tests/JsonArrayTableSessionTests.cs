using System.Text;
using Argonaut.Features.Json;

namespace Argonaut.Tests;

/// <summary>
/// The array-table session's lifetime contract, in the shape of JsonDiffSessionTests: the task
/// the document waits on is the ELEMENT index's (not the token scan's, which finishes one stride
/// early), disposal joins the element walk before the mapping is released in every interleaving,
/// double dispose is a no-op, and TearingDown fires from either end.
/// </summary>
public class JsonArrayTableSessionTests
{
    private static string WriteTempJson(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    /// <summary>A file big enough that indexing takes real time, so an immediate dispose lands
    /// mid-scan - the technique IndexedFileSessionTests and JsonDiffSessionTests both use.</summary>
    private static string WriteLargeTempJson(int elements = 400_000)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < elements; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"i\":").Append(i).Append(",\"s\":\"value-").Append(i).Append("\"}");
        }

        sb.Append(']');
        return WriteTempJson(sb.ToString());
    }

    [Fact]
    public async Task IndexingTask_IsTheElementWalksNotTheTokenScans()
    {
        // The distinction is the whole reason this type exists: the token scan completing is not
        // the moment the table stops growing.
        string path = WriteTempJson("[1,2,3]");
        try
        {
            using var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);

            Assert.Same(session.Elements.IndexingTask, session.IndexingTask);

            await session.IndexingTask;
            Assert.Equal(3, session.Elements.ElementCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAfterCompletion_ReleasesTheMapping()
    {
        string path = WriteTempJson("""[{"a":1},{"a":2}]""");
        try
        {
            var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);
            await session.IndexingTask;

            var file = session.Inner.File;
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => file.GetSpan(0, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DisposeMidScan_JoinsTheElementWalkBeforeReleasingTheMapping()
    {
        // If the mapping were released while the walk still ran, the walk would read freed
        // memory. The join is what makes that impossible, and this is the interleaving that
        // would catch its absence.
        string path = WriteLargeTempJson();
        try
        {
            var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);
            var walk = session.IndexingTask;

            session.Dispose();

            Assert.True(walk.IsCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DoubleDispose_IsANoOp()
    {
        string path = WriteTempJson("[1,2,3]");
        try
        {
            var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);

            session.Dispose();
            session.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RequestStop_IsIdempotentIncludingAfterDispose()
    {
        string path = WriteTempJson("[1,2,3]");
        try
        {
            var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);

            session.RequestStop();
            session.RequestStop();
            session.Dispose();
            session.RequestStop();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TearingDown_FiresOnRequestStop()
    {
        string path = WriteLargeTempJson();
        try
        {
            using var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);
            Assert.False(session.TearingDown.IsCancellationRequested);

            session.RequestStop();

            Assert.True(session.TearingDown.IsCancellationRequested);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TearingDown_AlsoFiresWhenTeardownStartsAtTheInnerSession()
    {
        // The token is linked over the file session's, so a stop that starts down there still
        // stops a UI-thread reader that linked this one.
        string path = WriteLargeTempJson();
        try
        {
            using var session = JsonArrayTableSession.Start(path, 0, new FileInfo(path).Length);

            session.Inner.RequestStop();

            Assert.True(session.TearingDown.IsCancellationRequested);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SubRange_IndexesOnlyTheArrayItWasGiven()
    {
        // The point of the sub-range mapping: the table's own document is the array, so its
        // root token is the array and its elements are the array's, not the outer document's.
        string json = """{"before":[9,9,9,9,9],"items":[10,20,30]}""";
        string path = WriteTempJson(json);
        try
        {
            int offset = json.IndexOf("[10", StringComparison.Ordinal);
            int length = json.IndexOf(']', offset) + 1 - offset;

            using var session = JsonArrayTableSession.Start(path, offset, length);
            await session.IndexingTask;

            Assert.Equal(3, session.Elements.ElementCount);
            Assert.Equal(JsonTokenKind.StartArray, session.Inner.Index.GetToken(0).Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MalformedRange_SurfacesTheTokenScansFailure()
    {
        // A byte range that isn't a whole JSON value: the token scan is what fails, and its
        // failure is the one worth reporting (it carries a location).
        string path = WriteTempJson("[1,2,3]");
        try
        {
            using var session = JsonArrayTableSession.Start(path, 0, 4); // "[1,2"
            try
            {
                await session.IndexingTask;
            }
            catch
            {
                // The walk stops when its source does; the failure is read back below.
            }

            Assert.NotNull(session.Failure);
            Assert.NotNull(session.Inner.Failure);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
