using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// A long session over a large XML tree must not keep anything per row it has shown. The raw view
/// has the same guard (<c>RawViewVirtualizationTests</c>); this one catches a cursor, cache or path
/// accumulator that grows with distance travelled instead of staying sized by the viewport.
///
/// Four phases seek to rows spread across the document, then step, paint and spell the path of
/// each. Every phase visits the same rows, so the bounded caches fill during the first; anything
/// the heap gains afterwards was kept by a visit and never let go.
/// </summary>
public class XmlTreeFootprintTests
{
    private const int VisitsPerPhase = 2_000;
    private const int RowsPerVisit = 6;
    private const long CorpusBytes = 8 * 1024 * 1024;

    /// <summary>Depth of a RecordRoot corpus's deepest row - the coordinates of a polygon.</summary>
    private const int DeepestRow = 9;

    /// <summary>Room for a cache that fills between the first and second phase, and for the GC's
    /// own noise. A leak of even a few hundred bytes per visit passes this in phase two alone, but
    /// not by phase three.</summary>
    private const long HeapSlack = 8_000_000;

    [Fact]
    public async Task NavigatingAndPaintingAcrossTheDocument_KeepsItsFootprintBounded()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xml-footprint-{Guid.NewGuid():N}.xml");
        XmlShapeCorpus.Write(path, XmlShape.RecordRoot, CorpusBytes);

        using var file = new MMapFile(path);
        try
        {
            var index = XmlSparseIndex.StartIndexing(file);
            await index.IndexingTask;
            Assert.Null(index.Failure);

            var reader = new XmlTreeReader(file);
            var text = new XmlTreeText(file, index.Structure, reader);
            var expand = new TreeExpandState(XmlViewSettings.DefaultExpandDepth);
            var cursor = new TreeCursor(index.Structure, reader, expand);
            var painter = new XmlTreePainter(text, ValueHintProviders.Standard(new DateHintSettings(), TimeProvider.System));
            var runs = new List<TreeRun>();
            long documentLength = file.AvailableLength;

            void Visit(int seed)
            {
                cursor.SeekTo(seed * 7_919L % documentLength);

                for (int row = 0; row < RowsPerVisit; row++)
                {
                    if (!cursor.MoveNext())
                        cursor.MoveToStart();

                    Assert.InRange(cursor.Current.Depth, 0, DeepestRow);

                    runs.Clear();
                    painter.AppendRuns(cursor.Current, runs);
                    Assert.NotEmpty(runs);

                    // What the path bar does when the selection moves: spell it and join it.
                    _ = XmlTreePaths.Format(XmlTreePaths.Segments(cursor, text, index));
                }
            }

            long settled = long.MaxValue;
            var phases = new List<string>();
            for (int phase = 0; phase < 4; phase++)
            {
                for (int visit = 0; visit < VisitsPerPhase; visit++)
                    Visit(visit);

                long heap = LiveHeap();
                phases.Add($"phase {phase}: {heap / 1024.0 / 1024.0:F2}MB");

                // Phase 0 pays the one-off costs and is not a baseline. Afterwards, measured
                // against the lowest settled reading, so one noisy phase cannot make the next look
                // like growth.
                if (phase >= 1)
                {
                    if (phase > 1)
                        Assert.True(heap < settled + HeapSlack,
                            $"heap climbed with rows visited - {string.Join(" | ", phases)}");

                    settled = Math.Min(settled, heap);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static long LiveHeap()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
