using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

public sealed class JsonViewModel
{
    public ObservableCollection<JsonNode> Nodes { get; } = new();

    public JsonViewModel()
    {
    }

    public async Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        var nodes = await Task.Run(() =>
        {
            using var mmap = new MMapFile(path);
            var tokens = JsonIndexer.Build(mmap, progressReporter);
            var builtNodes = new List<JsonNode>(tokens.Count);

            foreach (var t in tokens)
            {
                if (t.Text is null)
                    continue;

                builtNodes.Add(new JsonNode
                {
                    Depth = t.Depth,
                    Text = t.Text
                });
            }

            return builtNodes;
        });

        Nodes.Clear();
        foreach (var node in nodes)
            Nodes.Add(node);
    }
}
