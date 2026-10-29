using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Xml;

/// <summary>
/// The XML view model's side of value hints: its tree is painted with them, a chip that expands
/// decodes what the text hides - with the text's entities decoded first - and an address opens
/// through the system launcher.
/// </summary>
public sealed class XmlViewModelHintTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"hints-{Guid.NewGuid():N}.xml");

    public void Dispose() => File.Delete(path);

    private static string Jwt()
    {
        static string Part(string json) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        return $"{Part("""{"alg":"HS256"}""")}.{Part("""{"sub":"u1","exp":2000000000}""")}.c2ln";
    }

    private async Task<XmlViewModel> Load(string xml, RecordingSystemLauncher? launcher = null)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(xml));
        var vm = new XmlViewModel(new XmlViewSettings(), launcher);
        await vm.LoadAsync(path);
        await vm.IndexingTask;
        return vm;
    }

    /// <summary>Every run of every row, everything expanded.</summary>
    private static List<TreeRun> AllRuns(XmlViewModel vm)
    {
        var tree = vm.Tree!;
        var cursor = tree.NewCursor();
        var runs = new List<TreeRun>();
        foreach (var row in Rows(tree, cursor))
            tree.Painter.AppendRuns(row, runs);
        return runs;
    }

    private static IEnumerable<TreeRow> Rows(TreeDocument tree, ITreeRowCursor cursor)
    {
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
        {
            if (cursor.Current.Shape == TreeRowShape.Open && !cursor.Current.IsExpanded)
            {
                tree.SetExpanded(cursor.Current, true);
                cursor.SeekTo(cursor.Current.Start);
            }

            yield return cursor.Current;
        }
    }

    [Fact]
    public async Task AnExpandingChipDecodesWhatTheTextHides()
    {
        using var vm = await Load($"<r><token>{Jwt()}</token></r>");

        var link = AllRuns(vm).Select(r => r.Link).OfType<ExpandHintLink>().Single();
        var shown = await vm.PreviewHintAsync(link);

        Assert.NotNull(shown);
        shown!.Preview.Dispose();
    }

    [Fact]
    public async Task EmbeddedJsonIsDecodedFromItsEntities()
    {
        using var vm = await Load("<r><payload>{&quot;id&quot;: 1, &quot;tags&quot;: [&quot;a&quot;, &quot;b&quot;]}</payload></r>");

        var link = AllRuns(vm).Select(r => r.Link).OfType<ExpandHintLink>().Single();
        var shown = await vm.PreviewHintAsync(link);

        Assert.NotNull(shown);
        shown!.Preview.Dispose();
    }

    [Fact]
    public async Task AnAddressOpensThroughTheLauncher()
    {
        var launcher = new RecordingSystemLauncher();
        using var vm = await Load("<r><site>https://example.org/a?x=1&amp;y=2</site></r>", launcher);

        var link = AllRuns(vm).Select(r => r.Link).OfType<OpenUrlLink>().First();
        vm.OpenAddress(link.Address);

        Assert.Equal([new Uri("https://example.org/a?x=1&y=2")], launcher.AddressesOpened);
    }

    [Fact]
    public async Task TheToolbarOffersTheDateHintSettings()
    {
        using var vm = await Load("<r><d>2020-12-02</d></r>");

        var toolbar = Assert.IsType<XmlToolbarViewModel>(vm.Toolbar);
        Assert.Same(vm.HintSettings, toolbar.HintSettings);
    }
}
