using System.ComponentModel;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Settings;
using Argonaut.Shell;
using Argonaut.Tests.Support;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;

namespace Argonaut.Tests.Shell;

/// <summary>
/// The status bar's line endings are a fact about the input, not the view: counted once per
/// origin the shell adopts, kept across view switches, and dropped for a comparison of two.
/// </summary>
public class LineEndingStatusTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("argonaut-line-endings-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task OpeningAFile_ShowsItsLineEndings()
    {
        var detector = new FakeLineEndingDetector
        {
            Answer = new LineEndingTally(Lf: 0, CrLf: 1_234, LoneCr: 0, SampledBytes: 1024 * 1024, WholeInput: false),
        };
        var (vm, path) = await OpenAsync(detector);

        Assert.Equal("CRLF", vm.LineEndingLabel);
        Assert.Equal("Line endings in the first 1 MB\nCRLF: 1,234", vm.LineEndingToolTip);
        Assert.Equal(path, Assert.Single(detector.Asked).Path);
    }

    [Fact]
    public async Task AMixedFile_IsLabelledMixed_WithEachKindCounted()
    {
        var detector = new FakeLineEndingDetector { Answer = new LineEndingTally(Lf: 10, CrLf: 2, LoneCr: 0, SampledBytes: 100, WholeInput: true) };
        var (vm, _) = await OpenAsync(detector);

        Assert.Equal("Mixed", vm.LineEndingLabel);
        Assert.Equal("Line endings in the whole file\nLF: 10\nCRLF: 2", vm.LineEndingToolTip);
    }

    [Fact]
    public async Task AFileWithNoLineBreaks_ShowsNoLabel()
    {
        var detector = new FakeLineEndingDetector { Answer = new LineEndingTally(0, 0, 0, 40, true) };
        var (vm, _) = await OpenAsync(detector);

        Assert.Null(vm.LineEndingLabel);
        Assert.Null(vm.LineEndingToolTip);
    }

    [Fact]
    public async Task SwitchingTheView_DoesNotCountAgain()
    {
        var detector = new FakeLineEndingDetector();
        var (vm, _) = await OpenAsync(detector);

        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Ndjson);

        Assert.Single(detector.Asked);
        Assert.Equal("LF", vm.LineEndingLabel);
    }

    [Fact]
    public async Task OpeningAnotherFile_StopsTheFirstCount_AndShowsOnlyTheSecond()
    {
        var detector = new FakeLineEndingDetector { HoldsResults = true };
        var vm = CreateViewModel(detector);

        await vm.OpenPathAsync(WriteFile("a.json"));
        Assert.Equal(1, detector.Pending);

        await vm.OpenPathAsync(WriteFile("b.json"));
        Assert.Equal(2, detector.Asked.Count);
        Assert.Equal(1, detector.Pending); // the first was told to stop
        Assert.Null(vm.LineEndingLabel);

        detector.Complete(new LineEndingTally(0, 0, 5, 5, true));
        await WaitForAsync(() => vm.LineEndingLabel is not null);

        Assert.Equal("CR", vm.LineEndingLabel);
    }

    [Fact]
    public async Task ClosingTheFile_ClearsTheLabel()
    {
        var detector = new FakeLineEndingDetector();
        var (vm, _) = await OpenAsync(detector);

        await vm.CloseFileAsync();

        Assert.Null(vm.LineEndingLabel);
    }

    [Fact]
    public async Task AComparison_ShowsNoLabel()
    {
        var detector = new FakeLineEndingDetector();
        var vm = CreateViewModel(detector);

        await vm.OpenDiffAsync(
            new MemoryByteOrigin(Encoding.UTF8.GetBytes("{\"a\":1}\n"), "left.json"),
            new MemoryByteOrigin(Encoding.UTF8.GetBytes("{\"a\":2}\n"), "right.json"));

        Assert.Empty(detector.Asked);
        Assert.Null(vm.LineEndingLabel);
    }

    [Fact]
    public async Task TheStatusLine_IsTheLocationThenTheViewsDetail()
    {
        var (vm, path) = await OpenAsync(new FakeLineEndingDetector(), detail: "42 rows");

        Assert.Equal(DocumentStatusLine.Compose(path, "42 rows"), vm.StatusText);
    }

    [Fact]
    public async Task ADocumentThatWritesBreaks_IsGivenTheCount()
    {
        var detector = new FakeLineEndingDetector { Answer = new LineEndingTally(0, 4, 0, 40, true) };
        var vm = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (_, origin, _) => Task.FromResult<IDocumentViewModel>(new WritingDocument(origin)),
            lineEndings: detector);

        await vm.OpenPathAsync(WriteFile("doc.txt"));

        var document = Assert.IsType<WritingDocument>(vm.CurrentDocument);
        Assert.Equal(LineEndingStyle.CrLf, Assert.Single(document.Given).Style);
    }

    [Fact]
    public async Task ADocumentSwitchedInOverTheSameInput_IsGivenTheCountAlreadyMade()
    {
        var detector = new FakeLineEndingDetector();
        var vm = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (kind, origin, _) => Task.FromResult<IDocumentViewModel>(
                kind == FileTypeDetector.FileKind.Unidentified ? new WritingDocument(origin) : new Document(origin, "json")),
            lineEndings: detector);
        await vm.OpenPathAsync(WriteFile("doc.json"));

        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);

        var document = Assert.IsType<WritingDocument>(vm.CurrentDocument);
        Assert.Single(document.Given);
        Assert.Single(detector.Asked);
    }

    [Fact]
    public async Task APendingConversion_ShowsAsAnArrow_UntilCancelled()
    {
        var detector = new FakeLineEndingDetector { Answer = new LineEndingTally(3, 0, 0, 12, true) };
        var vm = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (_, origin, _) => Task.FromResult<IDocumentViewModel>(new WritingDocument(origin)),
            lineEndings: detector);
        await vm.OpenPathAsync(WriteFile("doc.txt"));
        Assert.True(vm.OffersLineEndingMenu);
        Assert.False(vm.ShowsLineEndingText);
        Assert.Equal(LineEndingStyle.Lf, vm.SavedLineEnding);

        vm.ConvertLineEndings(LineEndingStyle.CrLf);

        Assert.Equal("LF → CRLF", vm.LineEndingLabel);
        Assert.EndsWith("Converted to CRLF when saved", vm.LineEndingToolTip);
        Assert.Equal(LineEndingStyle.CrLf, vm.SavedLineEnding);

        vm.ConvertLineEndings(LineEndingStyle.Lf);

        Assert.Equal("LF", vm.LineEndingLabel);
    }

    [Fact]
    public async Task ADocumentWithNoBreaksYet_ShowsTheBreakItsNewLinesUse()
    {
        var detector = new FakeLineEndingDetector { Answer = new LineEndingTally(0, 0, 0, 0, true) };
        var vm = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (_, origin, _) => Task.FromResult<IDocumentViewModel>(new WritingDocument(origin)),
            lineEndings: detector);

        await vm.NewDocumentAsync();

        Assert.Equal("LF", vm.LineEndingLabel);
        Assert.Equal("No line breaks yet\nNew lines use LF", vm.LineEndingToolTip);
        Assert.Equal(LineEndingStyle.Lf, vm.SavedLineEnding);
    }

    [Fact]
    public async Task ADocumentThatCannotConvert_ShowsPlainText()
    {
        var (vm, _) = await OpenAsync(new FakeLineEndingDetector());

        Assert.True(vm.ShowsLineEndingText);
        Assert.False(vm.OffersLineEndingMenu);
    }

    private async Task<(MainWindowViewModel Vm, string Path)> OpenAsync(FakeLineEndingDetector detector, string detail = "loaded")
    {
        var vm = CreateViewModel(detector, detail);
        string path = WriteFile("doc.json");
        await vm.OpenPathAsync(path);
        return (vm, path);
    }

    private static MainWindowViewModel CreateViewModel(FakeLineEndingDetector detector, string detail = "loaded")
        => new(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (_, origin, _) => Task.FromResult<IDocumentViewModel>(new Document(origin, detail)),
            lineEndings: detector);

    private string WriteFile(string name)
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, "{\"a\":1}\n");
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }

    /// <summary>A document that writes line breaks: records the counts it is given, and holds a
    /// pending conversion the way the raw view does - choosing the input's own kind cancels it.</summary>
    private sealed class WritingDocument(IByteOrigin origin) : ILineEndingDocument, IDocumentViewModel
    {
        private LineEndingStyle? pending;

        public List<LineEndingTally> Given { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
        public IByteOrigin? Origin => origin;
        public string FilePath => origin.Path ?? origin.DisplayName;
        public string StatusDetail => "text";
        public IndexFailure? IndexFailure => null;
        public Task IndexingTask => Task.CompletedTask;
        public object? Toolbar => null;
        public ISearchNavigator? CreateSearchNavigator() => null;
        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;
        public void Dispose() { }

        public void UseInputLineEndings(LineEndingTally input) => Given.Add(input);
        public LineEndingStyle LineEnding => this.pending ?? Given.LastOrDefault().Prevailing ?? LineEndingStyle.Lf;
        public bool CanConvertLineEndings => true;
        public LineEndingStyle? PendingLineEnding => this.pending;

        public void ConvertLineEndingsOnSave(LineEndingStyle? target)
        {
            this.pending = Given.LastOrDefault().Style == target ? null : target;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingLineEnding)));
        }
    }

    private sealed class Document(IByteOrigin origin, string detail) : IDocumentViewModel
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public IByteOrigin? Origin => origin;
        public string FilePath => origin.Path ?? origin.DisplayName;
        public string StatusDetail => detail;
        public IndexFailure? IndexFailure => null;
        public Task IndexingTask => Task.CompletedTask;
        public object? Toolbar => null;
        public ISearchNavigator? CreateSearchNavigator() => null;
        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;
        public void Dispose() { }
    }
}
