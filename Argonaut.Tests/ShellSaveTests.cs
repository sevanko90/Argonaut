using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Saving;
using Argonaut.Engine.Search;
using Argonaut.Engine.Settings;
using Argonaut.Shell;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;
using Argonaut.Ui.Progress;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Tests;

/// <summary>
/// The shell's side of saving: which destination a save goes to, adopting it after a Save As,
/// reporting failure, and - the part that stops edits being lost - asking before anything would
/// drop them. The document is a fake that records what it was asked to do; the save itself is
/// <see cref="RawSaveTests"/>'s subject.
/// </summary>
[Collection("AppDataPaths")]
public sealed class ShellSaveTests : IDisposable
{
    private readonly string tempDir;

    public ShellSaveTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        AppDataPaths.RootOverride = Path.Combine(tempDir, "settings");
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string name = "notes.txt")
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, "plain text");
        return path;
    }

    private sealed class FakeNavigator : ISearchNavigator
    {
        public ScanTarget ScanTarget => throw new NotSupportedException();
        public void SetHighlightTerm(string? term) { }
        public Task RevealAsync(SearchMatch match, CancellationToken ct) => Task.CompletedTask;
        public CancellationToken DocumentTearingDown => default;
    }

    private sealed class SaveableDocument : ObservableObject, IDocumentViewModel, ISaveableDocument
    {
        private bool hasUnsavedChanges;

        public IByteOrigin? Origin { get; set; }

        public string FilePath => Origin?.Path ?? Origin?.DisplayName ?? string.Empty;

        public string StatusText => "loaded";

        public IndexFailure? IndexFailure => null;

        public Task IndexingTask => Task.CompletedTask;

        public object? Toolbar => null;

        public bool Disposed { get; private set; }

        public ISearchNavigator? CreateSearchNavigator() => new FakeNavigator();

        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;

        public void Dispose() => Disposed = true;

        public bool HasUnsavedChanges
        {
            get => hasUnsavedChanges;
            set => SetField(ref hasUnsavedChanges, value);
        }

        public bool CanSave => true;

        /// <summary>What the next save comes to.</summary>
        public DocumentSaveResult NextResult { get; set; } = DocumentSaveResult.Saved;

        public List<IByteOrigin> SavedTo { get; } = new();

        /// <summary>When set, a save runs until the shell stops it, then reports it stopped.</summary>
        public bool RunsUntilStopped { get; set; }

        public async Task<DocumentSaveResult> SaveAsync(IByteOrigin destination, IFileReplacer replacer, IProgressReporter? progress,
            CancellationToken stopping)
        {
            SavedTo.Add(destination);
            if (RunsUntilStopped)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stopping);
                }
                catch (OperationCanceledException)
                {
                }

                return DocumentSaveResult.Stopped;
            }

            if (NextResult.Outcome == DocumentSaveOutcome.Saved)
            {
                Origin = destination;
                HasUnsavedChanges = false;
            }

            return NextResult;
        }
    }

    /// <summary>A shell over one saveable document, with every dialog scripted and recorded.</summary>
    private sealed class Harness
    {
        public SaveableDocument Document { get; } = new();

        public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Cancel;

        public string? PickedPath { get; set; }

        public List<string> Asked { get; } = new();

        public List<string> Failures { get; } = new();

        public int ReplaceConfirmations { get; private set; }

        public MainWindowViewModel Shell { get; }

        public ProgressBoard Board { get; } = new(TimeProvider.System);

        public Harness(byte[]? clipboard = null)
        {
            Shell = new MainWindowViewModel(
                _ =>
                {
                    ReplaceConfirmations++;
                    return Task.FromResult(true);
                },
                readClipboardBytes: clipboard is null ? null : () => Task.FromResult<byte[]?>(clipboard),
                documentLoader: (_, origin, _) =>
                {
                    // The first load is the document under test; anything opened after it is a
                    // different, clean one.
                    if (Document.Origin is null)
                    {
                        Document.Origin = origin;
                        return Task.FromResult<IDocumentViewModel>(Document);
                    }

                    return Task.FromResult<IDocumentViewModel>(new SaveableDocument { Origin = origin });
                },
                pickSaveDestination: _ => Task.FromResult(PickedPath),
                askAboutUnsavedChanges: message =>
                {
                    Asked.Add(message);
                    return Task.FromResult(Choice);
                },
                reportFailure: message =>
                {
                    Failures.Add(message);
                    return Task.CompletedTask;
                },
                progressBoard: Board);
        }
    }

    [Fact]
    public async Task Save_GoesToTheOpenFile_ThroughItsOwnOrigin()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        var opened = harness.Document.Origin;
        harness.Document.HasUnsavedChanges = true;

        Assert.True(harness.Shell.CanSave);
        Assert.True(await harness.Shell.SaveAsync());

        Assert.Same(opened, Assert.Single(harness.Document.SavedTo));
        Assert.False(harness.Shell.CanSave);
    }

    [Fact]
    public async Task Save_WithNothingUnsaved_WritesNothing()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);

        Assert.True(await harness.Shell.SaveAsync());
        Assert.Empty(harness.Document.SavedTo);
    }

    [Fact]
    public async Task SavingAPaste_AsksWhereTo_AndTheDocumentThenHasThatPath()
    {
        var harness = new Harness(clipboard: Encoding.UTF8.GetBytes("pasted"));
        await harness.Shell.PasteAsync();
        var paste = harness.Document.Origin;
        harness.Document.HasUnsavedChanges = true;
        harness.PickedPath = Path.Combine(tempDir, "kept.txt");

        Assert.True(await harness.Shell.SaveAsync());

        var destination = Assert.Single(harness.Document.SavedTo);
        Assert.NotSame(paste, destination);
        Assert.Equal(harness.PickedPath, destination.Path);
        Assert.Equal(harness.PickedPath, harness.Shell.FilePath);
        Assert.Equal("kept.txt", harness.Shell.FileName);
        Assert.Contains(harness.Shell.RecentFiles, item => item.Path == harness.PickedPath);
    }

    [Fact]
    public async Task SaveAs_CancelledAtThePicker_SavesNothing()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;
        harness.PickedPath = null;

        Assert.False(await harness.Shell.SaveAsAsync());
        Assert.Empty(harness.Document.SavedTo);
        Assert.True(harness.Shell.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveAs_ToTheOpenFile_IsAnOrdinarySave()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        var opened = harness.Document.Origin;
        harness.Document.HasUnsavedChanges = true;
        harness.PickedPath = path;

        Assert.True(await harness.Shell.SaveAsAsync());
        Assert.Same(opened, Assert.Single(harness.Document.SavedTo));
    }

    [Fact]
    public async Task AFailedSave_IsReported_AndTheDocumentStays()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;
        harness.Document.NextResult = DocumentSaveResult.NotSaved("Couldn't save: locked.");

        Assert.False(await harness.Shell.SaveAsync());

        Assert.Equal("Couldn't save: locked.", Assert.Single(harness.Failures));
        Assert.Same(harness.Document, harness.Shell.CurrentDocument);
        Assert.True(harness.Shell.HasUnsavedChanges);
    }

    [Fact]
    public async Task ASaveThatLostTheDocument_ClosesIt_AndSaysWhereTheContentIs()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;
        harness.Document.NextResult = new DocumentSaveResult(DocumentSaveOutcome.NotSavedAndDocumentLost, "It is in /tmp/stage.");

        Assert.False(await harness.Shell.SaveAsync());

        Assert.Null(harness.Shell.CurrentDocument);
        Assert.Equal("It is in /tmp/stage.", Assert.Single(harness.Failures));
    }

    [Fact]
    public async Task Close_WithUnsavedChanges_AsksFirst_AndCancelKeepsTheDocument()
    {
        string path = WriteFile();
        var harness = new Harness { Choice = UnsavedChangesChoice.Cancel };
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;

        await harness.Shell.CloseFileAsync();

        Assert.Contains("notes.txt", Assert.Single(harness.Asked));
        Assert.Same(harness.Document, harness.Shell.CurrentDocument);
        Assert.False(harness.Document.Disposed);
    }

    [Fact]
    public async Task Close_WithUnsavedChanges_DontSave_Closes()
    {
        string path = WriteFile();
        var harness = new Harness { Choice = UnsavedChangesChoice.Discard };
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;

        await harness.Shell.CloseFileAsync();

        Assert.Null(harness.Shell.CurrentDocument);
        Assert.Empty(harness.Document.SavedTo);
    }

    [Fact]
    public async Task Close_WithUnsavedChanges_Save_SavesThenCloses()
    {
        string path = WriteFile();
        var harness = new Harness { Choice = UnsavedChangesChoice.Save };
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;

        await harness.Shell.CloseFileAsync();

        Assert.Single(harness.Document.SavedTo);
        Assert.Null(harness.Shell.CurrentDocument);
    }

    [Fact]
    public async Task Close_WhenTheChosenSaveFails_StaysOpen()
    {
        string path = WriteFile();
        var harness = new Harness { Choice = UnsavedChangesChoice.Save };
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;
        harness.Document.NextResult = DocumentSaveResult.NotSaved("Couldn't save.");

        await harness.Shell.CloseFileAsync();

        Assert.Same(harness.Document, harness.Shell.CurrentDocument);
        Assert.Single(harness.Failures);
    }

    [Fact]
    public async Task Close_WithNothingUnsaved_DoesNotAsk()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);

        await harness.Shell.CloseFileAsync();

        Assert.Empty(harness.Asked);
        Assert.Null(harness.Shell.CurrentDocument);
    }

    [Fact]
    public async Task OpeningAnotherFile_WithUnsavedChanges_AsksAboutThemInsteadOfReplace()
    {
        string first = WriteFile("first.txt");
        string second = WriteFile("second.txt");
        var harness = new Harness { Choice = UnsavedChangesChoice.Discard };
        await harness.Shell.OpenPathAsync(first);
        harness.Document.HasUnsavedChanges = true;

        await harness.Shell.OpenPathAsync(second);

        Assert.Contains("second.txt", Assert.Single(harness.Asked));
        Assert.Equal(0, harness.ReplaceConfirmations);
        Assert.Equal(second, harness.Shell.FilePath);
    }

    [Fact]
    public async Task SwitchingView_WithUnsavedChanges_Cancelled_StaysPut()
    {
        string path = WriteFile();
        var harness = new Harness { Choice = UnsavedChangesChoice.Cancel };
        await harness.Shell.OpenPathAsync(path);
        var kindBefore = harness.Shell.SelectedView;
        harness.Document.HasUnsavedChanges = true;

        await harness.Shell.SwitchViewAsync(FileTypeDetector.FileKind.Json);

        Assert.Single(harness.Asked);
        Assert.Same(harness.Document, harness.Shell.CurrentDocument);
        Assert.Equal(kindBefore, harness.Shell.SelectedView);
    }

    [Fact]
    public async Task StoppingASaveFromTheProgressBar_IsQuiet_AndKeepsTheChanges()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;
        harness.Document.RunsUntilStopped = true;

        ProgressEntry? saving = null;
        harness.Board.WorkStarted += (_, entry) =>
        {
            if (entry.Title.StartsWith("Saving"))
                saving = entry;
        };

        var save = harness.Shell.SaveAsync();
        Assert.NotNull(saving);
        Assert.Equal("Saving notes.txt", saving.Title);
        Assert.True(saving.CanStop);
        Assert.True(harness.Shell.IsSaving);

        saving.RequestStop();

        Assert.False(await save.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(harness.Failures);
        Assert.True(harness.Shell.HasUnsavedChanges);
        Assert.False(harness.Shell.IsSaving);
        Assert.True(saving.IsFinished);
        Assert.Same(harness.Document, harness.Shell.CurrentDocument);
    }

    /// <summary>The split button's two halves: Save needs something to save, Save As only needs a
    /// document ready to write - so on a clean document the dropdown works and Save does not.</summary>
    [Fact]
    public async Task SaveNeedsChanges_ButSaveAsDoesNot()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        var changed = new List<string?>();
        harness.Shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(harness.Shell.CanSaveAs);
        Assert.False(harness.Shell.CanSave);

        harness.Document.HasUnsavedChanges = true;

        Assert.True(harness.Shell.CanSave);
        Assert.Contains(nameof(MainWindowViewModel.CanSave), changed);
    }

    [Fact]
    public async Task WhileSaving_NeitherHalfIsAvailable()
    {
        string path = WriteFile();
        var harness = new Harness();
        await harness.Shell.OpenPathAsync(path);
        harness.Document.HasUnsavedChanges = true;
        harness.Document.RunsUntilStopped = true;

        ProgressEntry? saving = null;
        harness.Board.WorkStarted += (_, entry) => saving = entry;
        var save = harness.Shell.SaveAsync();

        Assert.False(harness.Shell.CanSaveAs);
        Assert.False(harness.Shell.CanSave);

        saving!.RequestStop();
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(harness.Shell.CanSaveAs);
    }
}
