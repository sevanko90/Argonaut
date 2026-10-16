using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Search;
using Argonaut.Ui.Find;

namespace Argonaut.Features.Csv;

/// <summary>
/// Single-stage reveal strategy for the CSV/TSV grid (no nested per-row view model to wait on,
/// unlike NdJsonSearchNavigator's two-stage reveal): resolve the match's byte offset to an
/// absolute file line via OffsetLineResolver - reused as-is rather than cloned, since CSV
/// rows and NDJSON lines are both backed by the identical FileOffsetIndex/FileLineSpan type and
/// there's nothing NDJSON-specific in that resolver - then re-split that row to find which
/// column the offset falls in, and hand both indices to the view model for the view to
/// scroll/select (the row vertically via ListBox.SelectedIndex, the column horizontally via the
/// grid's own ScrollViewer, which CsvSearchNavigator never touches directly).
/// </summary>
public sealed class CsvSearchNavigator : ISearchNavigator
{
    private readonly CsvViewModel viewModel;

    public CsvSearchNavigator(CsvViewModel viewModel)
    {
        this.viewModel = viewModel;
    }

    public ScanTarget ScanTarget => new ScanTarget(viewModel.Origin!);

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public CancellationToken DocumentTearingDown => viewModel.TearingDown;

    public async Task RevealAsync(SearchMatch match, CancellationToken ct)
    {
        var line = await OffsetLineResolver.ResolveWhenCoveredAsync(viewModel.Index!, match.Offset, ct);
        ct.ThrowIfCancellationRequested();
        if (line is not int lineIndex)
            return;

        var lineSpan = viewModel.Index!.GetLineSpan(lineIndex);
        var fieldSpans = CsvFieldReader.SplitToSpans(viewModel.Bytes!, lineSpan, viewModel.Delimiter);

        int columnIndex = 0;
        int occurrence = 0;
        for (int i = 0; i < fieldSpans.Length; i++)
        {
            if (match.Offset >= fieldSpans[i].Offset && match.Offset < fieldSpans[i].Offset + fieldSpans[i].Length)
            {
                columnIndex = i;
                occurrence = OccurrencesBefore(fieldSpans[i].Offset, match.Offset);
                break;
            }
        }

        // A match on the header line while IsHeaderRow is true has no data row to select, but
        // the column can still be revealed - the sticky header is pinned vertically, not
        // horizontally, so it can still be scrolled out of view sideways.
        int dataStartIndex = viewModel.DataStartIndex;
        int? rowIndex = lineIndex >= dataStartIndex ? lineIndex - dataStartIndex : null;

        viewModel.SelectRow(rowIndex, columnIndex, occurrence);
    }

    /// <summary>
    /// How many times the find term occurs in the field before the match - which of the cell's
    /// highlighted occurrences find is on. Counted in the field's raw text the way the cell's text
    /// is highlighted (case-insensitively, without overlaps); the quotes and doubled quotes a field
    /// may carry change nothing unless the term itself holds a quote.
    /// </summary>
    private int OccurrencesBefore(long fieldStart, long matchOffset)
    {
        if (matchOffset <= fieldStart || viewModel.HighlightTerm is not { Length: > 0 } term)
            return 0;

        string before = viewModel.Bytes!.GetUtf8String(fieldStart, (int)(matchOffset - fieldStart));
        int count = 0;
        foreach (var segment in SearchTextSplitter.Split(before, term) ?? [])
        {
            if (segment.IsMatch)
                count++;
        }

        return count;
    }
}
