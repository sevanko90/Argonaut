using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;



public sealed class NdJsonViewModel: IDisposable
{
    private readonly MMapFile mmap;
    private readonly IReadOnlyList<NdjsonLine> index;

    public WindowedLines VisibleLines { get; } = new();
    
    public NdJsonViewModel(string path)
    {
        this.mmap = new MMapFile(path);
        this.index = NdjsonIndexer.Build(mmap);
        //LoadWindow(0, 10);
    }

    public void LoadWindow(int startIndex, int count)
    {
        int end = Math.Min(startIndex + count, this.index.Count);

        var newItems = new List<string>(count);

        for (int i = startIndex; i < end; i++)
            newItems.Add(ReadLine(this.index[i]));

        VisibleLines.ReplaceAll(newItems);
    }

    private string ReadLine(NdjsonLine line)
    {
        var buf = new byte[line.Length];
        this.mmap.Read(line.Offset, buf);
        return Encoding.ASCII.GetString(buf);
    }

    /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
    public void Dispose()
    {
        this.mmap.Dispose();
    }
}