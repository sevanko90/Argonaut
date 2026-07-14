using System.Collections.ObjectModel;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

public sealed class JsonViewModel
{
    public ObservableCollection<JsonNode> Nodes { get; } = new();

    public JsonViewModel(string path)
    {
        using var mmap = new MMapFile(path);
        var tokens = JsonIndexer.Build(mmap);

        foreach (var t in tokens)
        {
            if (t.Text is not null)
            {
                Nodes.Add(new JsonNode
                {
                    Depth = t.Depth,
                    Text = t.Text
                });
            }
        }
    }
}