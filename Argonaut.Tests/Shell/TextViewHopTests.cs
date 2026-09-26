using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Saving;
using Argonaut.Engine.Search;
using Argonaut.Engine.Settings;
using Argonaut.Shell;
using Argonaut.Shell.Dialogs;
using Argonaut.Tests.Support;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Find;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Tests.Shell;

/// <summary>
/// A view switch carries the position across as a byte range - the outgoing document's selection
/// is what the incoming one is asked to reveal. Documents are fakes recording what they were asked
/// to reveal, so no real indexing is involved.
/// </summary>
public sealed class TextViewHopTests : IDisposable
{
    private readonly string tempDir;

    public TextViewHopTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeNavigator : ISearchNavigator
    {
        public ScanTarget ScanTarget => throw new NotSupportedException();
        public void SetHighlightTerm(string? term) { }
        public Task RevealAsync(SearchMatch match, CancellationToken ct) => Task.CompletedTask;
        public CancellationToken DocumentTearingDown => default;
    }

    private sealed class NavigableDocument : ObservableObject, IDocumentViewModel, IByteRangeNavigable, ISaveableDocument
    {
        private bool hasUnsavedChanges;

        public required FileTypeDetector.FileKind Kind { get; init; }

        public IByteOrigin? Origin => null;

        public string FilePath => string.Empty;

        public string StatusText => "loaded";

        public IndexFailure? IndexFailure => null;

        public Task IndexingTask => Task.CompletedTask;

        public object? Toolbar => null;

        public ISearchNavigator? CreateSearchNavigator() => new FakeNavigator();

        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;

        public void Dispose() { }

        public ByteRange? SelectedByteRange { get; set; }

        public List<ByteRange> Revealed { get; } = new();

        public Task RevealByteRangeAsync(ByteRange range)
        {
            Revealed.Add(range);
            return Task.CompletedTask;
        }

        public bool HasUnsavedChanges
        {
            get => hasUnsavedChanges;
            set => SetField(ref hasUnsavedChanges, value);
        }

        public bool CanSave => true;

        public Task<DocumentSaveResult> SaveAsync(IByteOrigin destination, IFileReplacer replacer, IProgressReporter? progress,
            CancellationToken stopping) => Task.FromResult(DocumentSaveResult.Saved);
    }

    /// <summary>A shell whose every load builds a fresh document of the kind asked for, recorded
    /// in <see cref="Loaded"/>.</summary>
    private sealed class Harness
    {
        public List<NavigableDocument> Loaded { get; } = new();

        public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Discard;

        public MainWindowViewModel Shell { get; }

        public NavigableDocument Current => (NavigableDocument)Shell.CurrentDocument!;

        public Harness()
        {
            Shell = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
                documentLoader: (kind, _, _) =>
                {
                    var document = new NavigableDocument { Kind = kind };
                    Loaded.Add(document);
                    return Task.FromResult<IDocumentViewModel>(document);
                },
                askAboutUnsavedChanges: _ => Task.FromResult(Choice));
        }
    }

    [Fact]
    public async Task SwitchToText_FromJson_RevealsTheSelectedNode()
    {
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(WriteFile("doc.json", "{\"a\":[1,2]}"));
        harness.Current.SelectedByteRange = new ByteRange(5, 5);

        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);

        Assert.Equal(FileTypeDetector.FileKind.Unidentified, harness.Current.Kind);
        Assert.Equal([new ByteRange(5, 5)], harness.Current.Revealed);
    }

    [Fact]
    public async Task SwitchBackToJson_FromText_RevealsTheCaret()
    {
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(WriteFile("doc.json", "{\"a\":[1,2]}"));
        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);
        harness.Current.SelectedByteRange = ByteRange.At(7);

        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Json);

        Assert.Equal(FileTypeDetector.FileKind.Json, harness.Current.Kind);
        Assert.Equal([ByteRange.At(7)], harness.Current.Revealed);
    }

    [Fact]
    public async Task Switch_WithNothingSelected_RevealsNothing()
    {
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(WriteFile("doc.json", "{\"a\":[1,2]}"));

        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);

        Assert.Empty(harness.Current.Revealed);
    }

    /// <summary>Discarded edits are still on screen when the switch happens, and their offsets
    /// describe text the new view will never show.</summary>
    [Fact]
    public async Task Switch_AfterDiscardingEdits_DoesNotCarryThePosition()
    {
        var harness = new Harness { Choice = UnsavedChangesChoice.Discard };
        await harness.Shell.OpenPathAsync(WriteFile("doc.json", "{\"a\":[1,2]}"));
        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);
        harness.Current.SelectedByteRange = ByteRange.At(7);
        harness.Current.HasUnsavedChanges = true;

        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Json);

        Assert.Equal(FileTypeDetector.FileKind.Json, harness.Current.Kind);
        Assert.Empty(harness.Current.Revealed);
    }

    [Fact]
    public async Task RevealInTextView_AlreadyInTheTextView_RevealsWithoutReloading()
    {
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(WriteFile("doc.json", "{\"a\":[1,2]}"));
        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);
        var text = harness.Current;

        await harness.Shell.RevealInTextViewAsync(new ByteRange(2, 4));

        Assert.Same(text, harness.Current);
        Assert.Equal([new ByteRange(2, 4)], text.Revealed);
    }

    [Fact]
    public async Task RevealInTextView_FromJson_SwitchesAndRevealsTheRangeAsked()
    {
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(WriteFile("doc.json", "{\"a\":[1,2]}"));
        harness.Current.SelectedByteRange = new ByteRange(1, 3);

        await harness.Shell.RevealInTextViewAsync(new ByteRange(5, 5));

        Assert.Equal(FileTypeDetector.FileKind.Unidentified, harness.Current.Kind);
        Assert.Equal([new ByteRange(5, 5)], harness.Current.Revealed);
    }
}
