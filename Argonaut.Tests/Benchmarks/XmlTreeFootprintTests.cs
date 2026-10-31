using System.Runtime.CompilerServices;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Tree;
using Xunit.Abstractions;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// The XML tree over a large document must not leak, either slowly across a long session or when
/// the document closes. The raw view has the same guard (<c>RawViewVirtualizationTests</c>).
///
/// <b>Slow leak.</b> A session is a run of visits: seek to a row the way a search hit lands, step,
/// paint and spell the path of a few rows. Every visit goes to rows no earlier visit touched,
/// because a leak through a per-row cache only grows on rows it has not seen - revisiting the same
/// rows would find them cached and show nothing. Warm-up phases fill the bounded caches; after them
/// the live heap is read after each phase, and what is asserted is its <i>slope</i> per visit,
/// fitted across all phases, so one noisy reading cannot pass or fail the test alone. Allocation is
/// measured too: a phase must not allocate more than the first one did, which is what work that
/// grows with session length (re-copying an accumulated list, say) looks like before it leaks.
///
/// <b>Close.</b> Opening, using and closing a document must give everything back: the index, the
/// session, the cursor and the text must all be collectable, and the live heap must return to
/// where it was before the document opened.
/// </summary>
public class XmlTreeFootprintTests(ITestOutputHelper output)
{
    private const long CorpusBytes = 8 * 1024 * 1024;
    private const int RowsPerVisit = 6;
    private const int VisitsPerPhase = 2_000;
    private const int MeasuredPhases = 6;

    /// <summary>The bounded caches are still filling through the second phase of a session
    /// (about 57 KiB on RecordRoot, then flat for sixteen phases), so three are spent warming up.</summary>
    private const int WarmUpPhases = 3;

    /// <summary>Depth of a RecordRoot corpus's deepest row - the coordinates of a polygon.</summary>
    private const int DeepestRow = 9;

    /// <summary>The live heap may grow by at most this per visit (six rows), fitted over
    /// 12,000 visits. A leak of a single small object per row (24 bytes and up, so 144 per visit)
    /// is well over it; measured, a healthy session reads within ±1.</summary>
    private const double MaxGrowthPerVisit = 16;

    /// <summary>What may be left on the heap after the document closes: the GC's own noise,
    /// not a document's worth of anything.</summary>
    private const long MaxLeftAfterClose = 64 * 1024;

    [Fact]
    public void ALongSessionAcrossTheDocument_HoldsNothingPerVisit()
    {
        string path = WriteCorpus();
        try
        {
            using var document = Document.Open(path);
            long documentLength = document.Bytes.AvailableLength;
            int nextSeed = 0;

            // Warm-up: JIT, statics and the bounded caches fill here and are not measured.
            for (int visit = 0; visit < WarmUpPhases * VisitsPerPhase; visit++)
                document.Visit(nextSeed++, documentLength);

            var heaps = new double[MeasuredPhases];
            var allocations = new long[MeasuredPhases];
            for (int phase = 0; phase < MeasuredPhases; phase++)
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int visit = 0; visit < VisitsPerPhase; visit++)
                    document.Visit(nextSeed++, documentLength);

                allocations[phase] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                heaps[phase] = LiveHeap();
            }

            double growthPerVisit = Slope(heaps) / VisitsPerPhase;
            string report = string.Join(" | ", heaps.Select((heap, phase) =>
                $"phase {phase}: heap {heap / 1024:N0} KiB, allocated {allocations[phase] / VisitsPerPhase:N0} B/visit"));
            output.WriteLine($"{report}{Environment.NewLine}growth {growthPerVisit:F2} B/visit");

            Assert.True(growthPerVisit < MaxGrowthPerVisit,
                $"The heap grew {growthPerVisit:F1} bytes per visit across the session - {report}");
            Assert.All(allocations, allocated => Assert.True(allocated <= allocations[0] * 5 / 4,
                $"A visit allocated more as the session went on - {report}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClosingTheDocument_GivesBackEverythingItHeld()
    {
        string path = WriteCorpus();
        try
        {
            // One whole open-use-close first, so JIT, statics and pools are not counted as left over.
            OpenUseAndClose(path);

            long before = LiveHeap();
            var held = OpenUseAndClose(path);
            long after = LiveHeap();

            output.WriteLine($"left after close: {after - before:N0} B");
            Assert.All(held, reference => Assert.False(reference.IsAlive,
                $"{reference.Target?.GetType().Name} is still reachable after the document closed."));
            Assert.InRange(after - before, long.MinValue, MaxLeftAfterClose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Not inlined, so nothing it creates is still on the caller's stack when the caller
    /// collects; it returns only weak references to what it held.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] OpenUseAndClose(string path)
    {
        var document = Document.Open(path);
        long documentLength = document.Bytes.AvailableLength;
        for (int visit = 0; visit < 200; visit++)
            document.Visit(visit, documentLength);

        WeakReference[] held =
        [
            new(document.Session), new(document.Session.Index), new(document.Cursor), new(document.Text),
        ];
        document.Dispose();
        return held;
    }

    private static string WriteCorpus()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xml-footprint-{Guid.NewGuid():N}.xml");
        XmlShapeCorpus.Write(path, XmlShape.RecordRoot, CorpusBytes);
        return path;
    }

    /// <summary>Least-squares slope of the readings against their phase number.</summary>
    private static double Slope(double[] readings)
    {
        double meanX = (readings.Length - 1) / 2.0, meanY = readings.Average();
        double covariance = 0, variance = 0;
        for (int x = 0; x < readings.Length; x++)
        {
            covariance += (x - meanX) * (readings[x] - meanY);
            variance += (x - meanX) * (x - meanX);
        }

        return covariance / variance;
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

    /// <summary>An open XML document as the tree view holds one: a session owning the file and
    /// its index, and the reader, text, cursor and painter a screen is drawn with.</summary>
    private sealed class Document : IDisposable
    {
        private readonly XmlTreePainter painter;
        private readonly List<TreeRun> runs = new(128);

        private Document(IndexedSourceSession<XmlSparseIndex> session)
        {
            Session = session;
            var reader = new XmlTreeReader(session.Bytes);
            Text = new XmlTreeText(session.Bytes, session.Index.Structure, reader);
            Cursor = new TreeCursor(session.Index.Structure, reader, new TreeExpandState(XmlViewSettings.DefaultExpandDepth));
            painter = new XmlTreePainter(Text, ValueHintProviders.Standard(new DateHintSettings(), TimeProvider.System));
        }

        public IndexedSourceSession<XmlSparseIndex> Session { get; }

        public IByteSource Bytes => Session.Bytes;

        public XmlTreeText Text { get; }

        public TreeCursor Cursor { get; }

        public static Document Open(string path)
        {
            var session = IndexedSourceSession<XmlSparseIndex>.Start(new MMapFile(path), XmlSparseIndex.StartIndexing);
            session.IndexingTask.GetAwaiter().GetResult();
            Assert.Null(session.Failure);
            return new Document(session);
        }

        /// <summary>Seek to a row picked by <paramref name="seed"/>, then step, paint and spell
        /// the path of the rows from it - what a search hit and a short look around it do.
        /// 7,919 is prime, so distinct seeds land on distinct offsets for any document under
        /// 7,919 × 2^23 bytes.</summary>
        public void Visit(int seed, long documentLength)
        {
            Cursor.SeekTo(seed * 7_919L % documentLength);

            for (int row = 0; row < RowsPerVisit; row++)
            {
                if (!Cursor.MoveNext())
                    Cursor.MoveToStart();

                Assert.InRange(Cursor.Current.Depth, 0, DeepestRow);

                runs.Clear();
                painter.AppendRuns(Cursor.Current, runs);
                Assert.NotEmpty(runs);

                // What the path bar does when the selection moves: spell it and join it.
                _ = XmlTreePaths.Format(XmlTreePaths.Segments(Cursor, Text, Session.Index));
            }
        }

        public void Dispose() => Session.Dispose();
    }
}
