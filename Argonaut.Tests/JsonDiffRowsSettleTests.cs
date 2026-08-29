using System.Text;
using Argonaut.Features.Json.Diff;

namespace Argonaut.Tests;

/// <summary>
/// Regression: the diff document intermittently rendered the pre-diff PREVIEW of the left
/// document forever, and intermittently threw out of the row cache while a test read it.
///
/// Two separate causes, both in the window around the diff finishing:
///
///  * <see cref="JsonDiffRowCollection"/>'s constructor walked first and asked
///    "is the diff complete?" afterwards, so a diff that finished during that walk was read as
///    "complete already - no growth monitor needed" and the collection kept its preview rows
///    with nothing left to rebuild them. Sampled before the walk now.
///  * The monitor's final refresh resumes on the UI thread under a dispatcher and on a POOL
///    thread without one, so a dispatcher-free caller that awaited only the diff's own task
///    read the rows while that refresh rebuilt them. Tests await
///    <c>Rows.FinalRefreshTask</c> instead.
///
/// Both are scheduling races, so this asserts over enough loads to hit the window: before the
/// fixes it failed inside the first few dozen, usually inside ten.
/// </summary>
public class JsonDiffRowsSettleTests
{
    private const int Loads = 200;

    private static string WriteTemp(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task EveryLoad_SettlesOnDiffRows_NotThePreview()
    {
        for (int i = 0; i < Loads; i++)
        {
            string leftPath = WriteTemp("""{"a":1}""");
            string rightPath = WriteTemp("""{"a":1,"new":"hello"}""");
            var vm = new JsonDiffViewModel();
            try
            {
                await vm.LoadAsync(leftPath, rightPath);
                try { await vm.IndexingTask; } catch { }
                await vm.Rows.FinalRefreshTask;

                // Preview rows carry no Added record, so this both locates the change and
                // proves the collection left preview mode.
                vm.GoToNextDiff();
                Assert.NotNull(vm.SelectedPosition);

                var row = (JsonDiffRow)vm.Rows[vm.SelectedPosition!.Value]!;
                Assert.Equal(DiffStatus.Added, row.Status);
                Assert.Equal("new", row.Right!.Name);
            }
            finally
            {
                vm.Dispose();
                File.Delete(leftPath);
                File.Delete(rightPath);
            }
        }
    }
}
