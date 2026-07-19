using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Csv;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>
/// Single-stage reveal strategy for the CSV/TSV grid (no nested per-row view model to wait on,
/// unlike NdJsonSearchNavigator's two-stage reveal): resolve the match's byte offset to an
/// absolute file line via NdJsonOffsetLineResolver - reused as-is rather than cloned, since CSV
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

    public MMapFile File => viewModel.Mmap!;

    public void SetHighlightTerm(string? term) => viewModel.HighlightTerm = term;

    public async Task RevealAsync(SearchMatch match, CancellationToken ct)
    {
        var line = await NdJsonOffsetLineResolver.ResolveWhenCoveredAsync(viewModel.Index!, match.Offset, ct);
        ct.ThrowIfCancellationRequested();
        if (line is not int lineIndex)
            return;

        var lineSpan = viewModel.Index!.GetLineSpan(lineIndex);
        var fieldSpans = CsvFieldReader.SplitToSpans(viewModel.Mmap!, lineSpan, viewModel.Delimiter);

        int columnIndex = 0;
        for (int i = 0; i < fieldSpans.Length; i++)
        {
            if (match.Offset >= fieldSpans[i].Offset && match.Offset < fieldSpans[i].Offset + fieldSpans[i].Length)
            {
                columnIndex = i;
                break;
            }
        }

        // A match on the header line while IsHeaderRow is true has no data row to select, but
        // the column can still be revealed - the sticky header is pinned vertically, not
        // horizontally, so it can still be scrolled out of view sideways.
        int dataStartIndex = viewModel.IsHeaderRow ? 1 : 0;
        int? rowIndex = lineIndex >= dataStartIndex ? lineIndex - dataStartIndex : null;

        viewModel.SelectRow(rowIndex, columnIndex);
    }
}
