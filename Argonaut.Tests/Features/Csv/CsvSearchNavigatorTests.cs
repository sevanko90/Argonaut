using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Search;
using Argonaut.Features.Csv;

namespace Argonaut.Tests.Features.Csv;

/// <summary>
/// Verifies what a find reveal in the CSV view selects: the row and column a match is in, and
/// which of the term's occurrences in that cell it is - so the grid can mark the one find is on
/// when a cell holds several.
/// </summary>
public class CsvSearchNavigatorTests
{
    private static async Task<CsvViewModel> LoadAsync(string text)
    {
        var vm = new CsvViewModel();
        await vm.LoadAsync(new MemoryByteOrigin(Encoding.UTF8.GetBytes(text), "rows.csv"), (byte)',');
        await vm.IndexingTask;
        return vm;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Says_which_occurrence_in_the_cell_a_match_is(int nth, int occurrence)
    {
        const string text = "id,notes\n1,\"ab, AB and ab\"\n";
        var vm = await LoadAsync(text);
        try
        {
            vm.HighlightTerm = "ab";
            int offset = -1;
            for (int i = 0; i <= nth; i++)
                offset = text.IndexOf("ab", offset + 1, StringComparison.OrdinalIgnoreCase);

            await new CsvSearchNavigator(vm).RevealAsync(new SearchMatch(offset, 2), CancellationToken.None);

            Assert.Equal(0, vm.SelectedRowIndex);
            Assert.Equal(1, vm.SelectedColumnIndex);
            Assert.Equal(occurrence, vm.SelectedMatchOccurrence);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
