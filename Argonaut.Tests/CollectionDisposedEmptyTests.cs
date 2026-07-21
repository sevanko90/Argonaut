using System.Collections;
using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The virtualizing-list ItemsSources must report themselves empty once disposed. When a
/// document view is torn down (a ContentControl content swap on file close/switch), Avalonia
/// walks the outgoing ItemsSource one last time - synchronously, after the owning view model
/// (and its mapping) is disposed. If these collections still reported their live row count
/// there, that walk would string-allocate every row of a multi-GB file (a multi-second stall
/// on close) and, reading the just-unmapped file, crash with an access violation.
/// </summary>
public class CollectionDisposedEmptyTests
{
    private static string WriteTempFile(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static int CountViaEnumerator(IEnumerable source)
    {
        int n = 0;
        foreach (var _ in source) n++;
        return n;
    }

    [Fact]
    public async Task NdJsonLineCollection_AfterDispose_IsEmpty()
    {
        string path = WriteTempFile(string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"{{\"i\":{i}}}")));
        try
        {
            var vm = new NdJsonViewModel();
            await vm.LoadAsync(path);
            await vm.IndexingTask;
            var lines = vm.Lines;
            Assert.True(lines.Count > 0);

            vm.Dispose();

            Assert.Empty(lines);
            Assert.Equal(0, CountViaEnumerator(lines));
            Assert.Null(lines[0]); // no mmap read after the mapping is gone
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JsonRowCollection_AfterDispose_IsEmpty()
    {
        string path = WriteTempFile("{\"a\":1,\"b\":[1,2,3],\"c\":{\"d\":4}}");
        try
        {
            var vm = new JsonViewModel();
            await vm.LoadAsync(path);
            await vm.IndexingTask;
            var rows = vm.Rows;
            Assert.True(rows.Count > 0);

            vm.Dispose();

            Assert.Empty(rows);
            Assert.Equal(0, CountViaEnumerator(rows));
            Assert.Null(rows[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CsvRowCollection_AfterDispose_IsEmpty()
    {
        string path = WriteTempFile("a,b,c\n1,2,3\n4,5,6\n7,8,9\n");
        try
        {
            var vm = new CsvViewModel();
            await vm.LoadAsync(path, (byte)',');
            await vm.IndexingTask;
            var rows = vm.Rows;
            Assert.True(rows.Count > 0);

            vm.Dispose();

            Assert.Empty(rows);
            Assert.Equal(0, CountViaEnumerator(rows));
            Assert.Null(rows[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
