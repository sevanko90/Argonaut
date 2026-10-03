using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw;

/// <summary>
/// The raw toolbar's colour picker: that the choice only takes effect on the dispatcher's next
/// turn, and that it reaches the document, whose Auto resolves from the file's name or its text.
/// </summary>
public sealed class RawToolbarColoursTests : IDisposable
{
    private readonly string tempDir;

    public RawToolbarColoursTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task<RawViewModel> LoadAsync(string path)
    {
        var vm = new RawViewModel(new RawViewSettings());
        await vm.LoadAsync(path);
        await vm.IndexingTask;
        return vm;
    }

    [Fact]
    public void SettingTheIndex_RecordsItNowAndActsOnTheNextTurn()
    {
        using var ui = new DeferredUiScope();
        var applied = new List<RawColourChoice>();
        var toolbar = new RawToolbarViewModel(160, _ => { }, _ => { }, applied.Add);

        toolbar.ColoursIndex = (int)RawColourChoice.Config;

        Assert.Equal((int)RawColourChoice.Config, toolbar.ColoursIndex);
        Assert.Empty(applied);

        ui.Pump();
        Assert.Equal(new[] { RawColourChoice.Config }, applied);
    }

    [Fact]
    public void OutOfRangeAndRepeatedIndexes_AreIgnored()
    {
        using var ui = new DeferredUiScope();
        var applied = new List<RawColourChoice>();
        var toolbar = new RawToolbarViewModel(160, _ => { }, _ => { }, applied.Add);

        toolbar.ColoursIndex = -1; // a ComboBox raises -1 during teardown
        toolbar.ColoursIndex = 4;  // one past Config, the last entry
        toolbar.ColoursIndex = 0;  // already there
        ui.Pump();

        Assert.Empty(applied);
        Assert.Equal(0, toolbar.ColoursIndex);
    }

    [Fact]
    public void AutoLabel_NamesWhatItChose()
    {
        var toolbar = new RawToolbarViewModel(160, _ => { }, _ => { });
        Assert.Equal("Colours: Auto (none)", toolbar.AutoColoursLabel);

        toolbar.SetAutoColours("JSON");
        Assert.Equal("Colours: Auto (JSON)", toolbar.AutoColoursLabel);

        toolbar.SetAutoColours(null);
        Assert.Equal("Colours: Auto (none)", toolbar.AutoColoursLabel);
    }

    [Fact]
    public async Task AutoFollowsTheFileName()
    {
        using var vm = await LoadAsync(WriteFile("settings.yaml", "x: 1\n"));

        Assert.Same(RawLexerChoice.Config, vm.Lexer);
        Assert.Equal("Colours: Auto (Config)", ((RawToolbarViewModel)vm.Toolbar!).AutoColoursLabel);
    }

    [Fact]
    public async Task AutoSniffsAFileWhoseNameSaysNothing()
    {
        using var vm = await LoadAsync(WriteFile("data.txt", "{\"a\": 1}\n"));
        Assert.Same(RawLexerChoice.Json, vm.Lexer);

        using var plain = await LoadAsync(WriteFile("notes.txt", "hello there\nnothing to see\n"));
        Assert.Null(plain.Lexer);
        Assert.Equal("Colours: Auto (none)", ((RawToolbarViewModel)plain.Toolbar!).AutoColoursLabel);
    }

    [Fact]
    public async Task ThePickerReachesTheDocument()
    {
        using var ui = new DeferredUiScope();
        using var vm = await LoadAsync(WriteFile("a.json", "{\"a\": 1}\n"));
        var toolbar = (RawToolbarViewModel)vm.Toolbar!;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        toolbar.ColoursIndex = (int)RawColourChoice.Off;
        Assert.Same(RawLexerChoice.Json, vm.Lexer);

        ui.Pump();
        Assert.Null(vm.Lexer);
        Assert.Contains(nameof(RawViewModel.Lexer), changed);

        toolbar.ColoursIndex = (int)RawColourChoice.Config;
        ui.Pump();
        Assert.Same(RawLexerChoice.Config, vm.Lexer);

        toolbar.ColoursIndex = (int)RawColourChoice.Auto;
        ui.Pump();
        Assert.Same(RawLexerChoice.Json, vm.Lexer);
    }
}
