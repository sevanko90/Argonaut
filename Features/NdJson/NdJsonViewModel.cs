using System.Collections.ObjectModel;
using System.Text;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed class NdJsonViewModel
{
    public ObservableCollection<string> Lines { get; } = new();

    public NdJsonViewModel(string path)
    {
        using var mmap = new MMapFile(path);
        var index = NdjsonIndexer.Build(mmap);

        foreach (var line in index)
        {
            var buffer = new byte[line.Length];
            mmap.Read(line.Offset, buffer);
            Lines.Add(Encoding.UTF8.GetString(buffer).TrimEnd('\n', '\r'));
        }
    }
}