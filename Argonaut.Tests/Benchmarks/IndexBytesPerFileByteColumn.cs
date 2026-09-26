using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// Bytes allocated per operation divided by the document's size - for a benchmark that builds an
/// index and nothing else, the index's footprint as a multiple of the file. Reads the size from
/// the <c>SizeMiB</c> parameter, which <see cref="JsonShapeCorpus"/> honours exactly.
/// </summary>
public sealed class IndexBytesPerFileByteColumn : IColumn
{
    public string Id => nameof(IndexBytesPerFileByteColumn);
    public string ColumnName => "Alloc/FileByte";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Bytes allocated per operation divided by the document's size in bytes";

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        if (summary[benchmarkCase]?.GcStats.GetBytesAllocatedPerOperation(benchmarkCase) is not long allocated)
            return "-";
        if (benchmarkCase.Parameters["SizeMiB"] is not int sizeMiB)
            return "-";

        return (allocated / (sizeMiB * 1024.0 * 1024.0)).ToString("0.000");
    }

    /// <summary>Attaches the column through <c>[Config(typeof(IndexBytesPerFileByteColumn.Config))]</c>.</summary>
    public sealed class Config : ManualConfig
    {
        public Config() => AddColumn(new IndexBytesPerFileByteColumn());
    }
}
