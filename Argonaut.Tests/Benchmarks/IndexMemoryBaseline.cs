using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Raw;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// A point-in-time measurement of what indexing costs, per kind of document: how fast, how much it
/// allocates, how high memory peaks, what the finished index keeps, and whether anything is left
/// behind once it is released. Run with
/// <c>dotnet run -c Release --project Argonaut.Tests -- --index-memory [--size-mib N] [--processes N] [--out file.md]</c>.
///
/// Not BenchmarkDotNet: peak memory needs sampling while the scan runs, and a process's peak
/// working set is only meaningful for a process that did nothing else. So the parent writes
/// deterministic documents and runs each case in fresh child processes, each of which indexes a
/// small document of the same kind first (so JIT and type loading are not counted), then opens,
/// indexes and releases the real one three times, measuring each cycle.
///
/// A whole process can run slow - one run had JSON 3-16% off while the next two matched - so each
/// case runs in several processes (<c>--processes</c>, default 3) and speed is reported as the
/// median process with the range across them. Memory is the same from process to process, so it
/// is reported from the median process.
///
/// The working set includes the file's mapped pages once the scan has touched them. On macOS
/// those are clean and cost nothing to reclaim, so the managed heap is the number that reflects
/// the app's own memory; both are reported.
/// </summary>
public static class IndexMemoryBaseline
{
    private const int Cycles = 3;
    private const int DefaultProcesses = 3;
    private const int WarmupMiB = 4;

    private enum Kind
    {
        Text,
        Csv,
        NdJson,
        JsonRecords,
        JsonTokenDense,
        JsonRecordsWithHashes,
    }

    private static readonly (Kind Kind, string Indexer, string Describes)[] Cases =
    [
        (Kind.Text, "RawSegmentIndex (wrap 160)", "log lines, 60-200 bytes, every 5,000th 40 KB"),
        (Kind.Csv, "FileOffsetIndex", "12 columns, quoted text with commas and doubled quotes"),
        (Kind.NdJson, "FileOffsetIndex", "one record per line, nested object and array"),
        (Kind.JsonRecords, "JsonSparseIndex", "one root array of records (JsonShape.RecordArray)"),
        (Kind.JsonTokenDense, "JsonSparseIndex", "one root array of short numbers (JsonShape.TokenDenseArray)"),
        (Kind.JsonRecordsWithHashes, "JsonSparseIndex + content hashes", "as JSON records, indexed the way a diff indexes"),
    ];

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--case")
            return RunCase(Enum.Parse<Kind>(args[1]), args[2], args[3]);

        int sizeMiB = 256;
        int processes = DefaultProcesses;
        string? output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--size-mib")
                sizeMiB = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--processes")
                processes = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture));
            else if (args[i] == "--out")
                output = args[++i];
        }

        string directory = Path.Combine(Path.GetTempPath(), $"argonaut-index-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var results = new List<(Kind Kind, string Indexer, string Describes, List<CaseResult> Runs)>();
            foreach (var (kind, indexer, describes) in Cases)
            {
                string path = Path.Combine(directory, $"{kind}.dat");
                string warmup = Path.Combine(directory, $"{kind}-warmup.dat");
                Console.Error.WriteLine($"{kind}: writing {sizeMiB} MiB…");
                Write(kind, path, sizeMiB * 1024L * 1024L);
                Write(kind, warmup, WarmupMiB * 1024L * 1024L);

                var runs = new List<CaseResult>();
                for (int run = 1; run <= processes; run++)
                {
                    Console.Error.WriteLine($"{kind}: indexing, process {run} of {processes}…");
                    runs.Add(RunChild(kind, path, warmup));
                }

                results.Add((kind, indexer, describes, runs));
                File.Delete(path);
                File.Delete(warmup);
            }

            string report = Report(sizeMiB, processes, results);
            Console.WriteLine(report);
            if (output is not null)
                File.WriteAllText(output, report);
            return 0;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the child: one case, measured ---------------------------------------------------

    private sealed record Cycle(double Seconds, long Allocated, long PeakHeap, long PeakWorkingSet, long Retained,
        long AfterRelease, int Gen0, int Gen1, int Gen2, long Items);

    private sealed record CaseResult(long FileBytes, long BaselineHeap, long BaselineWorkingSet, List<Cycle> Cycles);

    private static CaseResult RunChild(Kind kind, string path, string warmup)
    {
        string assembly = typeof(IndexMemoryBaseline).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--index-memory");
        start.ArgumentList.Add("--case");
        start.ArgumentList.Add(kind.ToString());
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(warmup);

        using var process = Process.Start(start)!;
        string json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{kind} failed with exit code {process.ExitCode}.");

        return JsonSerializer.Deserialize<CaseResult>(json)!;
    }

    private static int RunCase(Kind kind, string path, string warmup)
    {
        // Warm-up: every code path of this kind jitted and every type loaded before measuring.
        Measure(kind, warmup);

        FullCollect();
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        long baselineWorkingSet = Environment.WorkingSet;

        var cycles = new List<Cycle>();
        for (int i = 0; i < Cycles; i++)
            cycles.Add(Measure(kind, path, baselineHeap));

        var result = new CaseResult(new FileInfo(path).Length, baselineHeap, baselineWorkingSet, cycles);
        Console.Out.Write(JsonSerializer.Serialize(result));
        return 0;
    }

    private static Cycle Measure(Kind kind, string path, long baselineHeap = 0)
    {
        var cycle = IndexAndRelease(kind, path, baselineHeap);

        // Measured here, not inside IndexAndRelease: there the session and its index would still
        // be locals of a live frame, and a collection could not tell a leak from the harness
        // holding them.
        FullCollect();
        return cycle with { AfterRelease = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap };
    }

    /// <summary>One cycle up to the release: indexes while sampling, measures what the index
    /// keeps, then releases it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Cycle IndexAndRelease(Kind kind, string path, long baselineHeap)
    {
        FullCollect();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        long peakHeap = 0, peakWorkingSet = 0;
        bool indexing = true;
        var sampler = new Thread(() =>
        {
            while (Volatile.Read(ref indexing))
            {
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
                peakWorkingSet = Math.Max(peakWorkingSet, Environment.WorkingSet);
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();

        var stopwatch = Stopwatch.StartNew();
        var (session, items) = Index(kind, path);
        stopwatch.Stop();

        Volatile.Write(ref indexing, false);
        sampler.Join();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
        peakWorkingSet = Math.Max(peakWorkingSet, Environment.WorkingSet);
        int collections0 = GC.CollectionCount(0) - gen0, collections1 = GC.CollectionCount(1) - gen1, collections2 = GC.CollectionCount(2) - gen2;

        // What the finished index keeps, the session still open.
        FullCollect();
        long retained = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
        long count = items();
        session.Dispose();

        return new Cycle(stopwatch.Elapsed.TotalSeconds, allocated, peakHeap - baselineHeap, peakWorkingSet, retained,
            AfterRelease: 0, collections0, collections1, collections2, count);
    }

    /// <summary>Opens and indexes a document the way its view does, to completion.</summary>
    private static (IDisposable Session, Func<long> Items) Index(Kind kind, string path)
    {
        switch (kind)
        {
            case Kind.Text:
            {
                var source = new MMapFile(path);
                var index = RawSegmentIndex.StartIndexing(source, RawViewSettings.DefaultWrapWidth);
                index.IndexingTask.GetAwaiter().GetResult();
                return (new Owned(() => source.Release()), () => index.RowCount);
            }

            case Kind.Csv:
            case Kind.NdJson:
            {
                var session = IndexedSourceSession<FileOffsetIndex>.Start(new MMapFile(path), FileOffsetIndex.StartIndexing);
                session.IndexingTask.GetAwaiter().GetResult();
                return (session, () => session.Index.LineCount);
            }

            default:
            {
                var session = IndexedSourceSession<JsonSparseIndex>.Start(new MMapFile(path),
                    kind == Kind.JsonRecordsWithHashes ? JsonSparseIndex.StartIndexingWithContentHashes : JsonSparseIndex.StartIndexing);
                session.IndexingTask.GetAwaiter().GetResult();
                return (session, () => session.Index.Structure.ContainerCount + session.Index.Structure.CheckpointCount
                    + (session.Index.ContentHashes?.RecordedCount ?? 0));
            }
        }
    }

    private sealed class Owned(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private static void FullCollect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // ---- the documents -------------------------------------------------------------------

    private static void Write(Kind kind, string path, long size)
    {
        switch (kind)
        {
            case Kind.JsonRecords:
            case Kind.JsonRecordsWithHashes:
                JsonShapeCorpus.Write(path, JsonShape.RecordArray, size);
                return;
            case Kind.JsonTokenDense:
                JsonShapeCorpus.Write(path, JsonShape.TokenDenseArray, size);
                return;
        }

        var random = new Random(20260926);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
        if (kind == Kind.Csv)
            writer.WriteLine("id,timestamp,level,region,account,amount,currency,quantity,ratio,flag,description,notes");

        var line = new StringBuilder();
        for (long row = 0; stream.Position + 64 * 1024 < size; row++)
        {
            line.Clear();
            switch (kind)
            {
                case Kind.Text: TextLine(line, random, row); break;
                case Kind.Csv: CsvLine(line, random, row); break;
                default: NdJsonLine(line, random, row); break;
            }

            writer.WriteLine(line);
            if (row % 4096 == 0)
                writer.Flush();
        }
    }

    private static readonly string[] Words =
        ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima",
         "request", "response", "timeout", "retry", "cache", "miss", "user", "order", "invoice", "shipment", "café", "naïve"];

    private static readonly string[] Levels = ["INFO", "INFO", "INFO", "DEBUG", "WARN", "ERROR"];

    private static void Sentence(StringBuilder line, Random random, int minWords, int maxWords)
    {
        int words = random.Next(minWords, maxWords);
        for (int i = 0; i < words; i++)
            line.Append(i == 0 ? "" : " ").Append(Words[random.Next(Words.Length)]);
    }

    private static void TextLine(StringBuilder line, Random random, long row)
    {
        var at = new DateTime(2026, 9, 26).AddMilliseconds(row * 37);
        line.Append(at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(' ').Append(Levels[random.Next(Levels.Length)])
            .Append(" [worker-").Append(random.Next(32)).Append("] ");
        if (row % 5000 == 4999)
            line.Append("payload=").Append('x', 40 * 1024);
        else
            Sentence(line, random, 4, 22);
    }

    private static void CsvLine(StringBuilder line, Random random, long row)
    {
        var at = new DateTime(2026, 1, 1).AddSeconds(row * 13);
        line.Append(row).Append(',')
            .Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
            .Append(Levels[random.Next(Levels.Length)]).Append(',')
            .Append("eu-west-").Append(random.Next(1, 4)).Append(',')
            .Append("ACC").Append(random.Next(100000, 999999)).Append(',')
            .Append((random.NextDouble() * 10000).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
            .Append(random.Next(3) switch { 0 => "EUR", 1 => "USD", _ => "GBP" }).Append(',')
            .Append(random.Next(1, 500)).Append(',')
            .Append(random.NextDouble().ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
            .Append(random.Next(2) == 0 ? "true" : "false").Append(",\"");
        Sentence(line, random, 2, 8);
        line.Append(", \"\"quoted\"\" part\",\"");
        Sentence(line, random, 0, 12);
        line.Append('"');
    }

    private static void NdJsonLine(StringBuilder line, Random random, long row)
    {
        line.Append("{\"id\":").Append(row)
            .Append(",\"level\":\"").Append(Levels[random.Next(Levels.Length)])
            .Append("\",\"amount\":").Append((random.NextDouble() * 10000).ToString("0.00", CultureInfo.InvariantCulture))
            .Append(",\"user\":{\"name\":\"");
        Sentence(line, random, 1, 3);
        line.Append("\",\"tags\":[");
        for (int i = random.Next(0, 5); i > 0; i--)
            line.Append('"').Append(Words[random.Next(Words.Length)]).Append(i > 1 ? "\"," : "\"");
        line.Append("]},\"message\":\"");
        Sentence(line, random, 3, 16);
        line.Append("\"}");
    }

    // ---- the report ----------------------------------------------------------------------

    /// <summary>A process's median cycle - the one its speed is read from.</summary>
    private static Cycle MedianCycle(CaseResult run) => run.Cycles.OrderBy(c => c.Seconds).ElementAt(run.Cycles.Count / 2);

    private static string Report(int sizeMiB, int processes, List<(Kind Kind, string Indexer, string Describes, List<CaseResult> Runs)> results)
    {
        var text = new StringBuilder();
        text.AppendLine($"Documents of {sizeMiB} MiB each; {processes} fresh processes per document, each running {Cycles}");
        text.AppendLine("open-index-release cycles after a warm-up on a small document of the same kind. Each process's");
        text.AppendLine("speed is its median cycle; the table shows the median process, with the range of speeds across");
        text.AppendLine("the processes. Allocation and what is kept come from that process; peaks are the highest of all.");
        text.AppendLine();
        text.AppendLine($"- Machine: {Machine()}");
        text.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, " +
                        $"{(GCSettings.IsServerGC ? "Server" : "Workstation")} GC");
        text.AppendLine();
        text.AppendLine("| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (kind, indexer, _, runs) in results)
        {
            var result = runs.OrderBy(run => MedianCycle(run).Seconds).ElementAt(runs.Count / 2);
            var cycles = result.Cycles;
            var median = MedianCycle(result);
            double mib = result.FileBytes / (1024.0 * 1024.0);
            double slowest = mib / runs.Max(run => MedianCycle(run).Seconds);
            double fastest = mib / runs.Min(run => MedianCycle(run).Seconds);
            text.Append($"| {kind} | {indexer} | {median.Items:N0} | {median.Seconds * 1000:N0} ms | {mib / median.Seconds:N0} MiB/s ")
                .Append($"| {slowest:N0}-{fastest:N0} ")
                .Append($"| {Bytes(median.Allocated)} | {(double)median.Allocated / result.FileBytes:0.0000} ")
                .Append($"| {median.Gen0}/{median.Gen1}/{median.Gen2} ")
                .Append($"| {Bytes(runs.Max(run => run.Cycles.Max(c => c.PeakHeap)))} | {Bytes(runs.Max(run => run.Cycles.Max(c => c.PeakWorkingSet)))} ")
                .Append($"| {Bytes(median.Retained)} | {Bytes(cycles[0].AfterRelease)} / {Bytes(cycles[^1].AfterRelease)} |")
                .AppendLine();
        }

        text.AppendLine();
        text.AppendLine("Documents:");
        text.AppendLine();
        foreach (var (kind, _, describes, runs) in results)
        {
            var result = runs.OrderBy(run => MedianCycle(run).Seconds).ElementAt(runs.Count / 2);
            text.AppendLine($"- **{kind}**: {describes}. Baseline before indexing: heap {Bytes(result.BaselineHeap)}, working set {Bytes(result.BaselineWorkingSet)}.");
        }

        text.AppendLine();
        text.AppendLine("Columns: *Peak heap* and *Index kept* and *Left after release* are managed heap above the");
        text.AppendLine("baseline - kept is after a full collection with the index still open, left after release is");
        text.AppendLine("after closing it and collecting again (what a leak would show as). *Peak working set* is the");
        text.AppendLine("whole process and includes the file's mapped pages, clean and free to reclaim on macOS.");
        return text.ToString();
    }

    private static string Machine()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var start = new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string hw.memsize") { RedirectStandardOutput = true };
                using var process = Process.Start(start)!;
                var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                process.WaitForExit();
                return $"{lines[0]}, {long.Parse(lines[1], CultureInfo.InvariantCulture) / (1024 * 1024 * 1024)} GB, {Environment.ProcessorCount} cores";
            }
        }
        catch
        {
            // Unknown machine: say what we can.
        }

        return $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores";
    }

    private static string Bytes(long bytes) => Math.Abs(bytes) switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };
}
