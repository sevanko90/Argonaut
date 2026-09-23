using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Tests;

/// <summary>
/// Saving from the raw editor, end to end over real files: the copy, the unmap-commit-reopen
/// sequence, and above all what happens when the commit fails - the file must be untouched and
/// the edits still open over it, or, if the file is gone too, the staged copy must survive.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawSaveTests : IDisposable
{
    private readonly string tempDir;

    public RawSaveTests()
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

    private string WriteFile(string content, string name = "doc.txt")
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private string[] Stages() => Directory.GetFiles(tempDir, $"*{SiblingFileReplacer.StageMarker}*");

    private static string DocumentText(RawViewModel vm)
    {
        var source = vm.Document!;
        var bytes = new byte[source.AvailableLength];
        source.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>A loaded, fully indexed document in edit mode, with <paramref name="typed"/>
    /// typed at <paramref name="at"/>.</summary>
    private static async Task<RawViewModel> EditedAsync(IByteOrigin origin, long at, string typed)
    {
        var vm = new RawViewModel();
        await vm.LoadAsync(origin);
        await vm.IndexingTask;
        vm.SetEditing(true);
        vm.Caret!.PlaceAt(at);
        Assert.True(vm.TypeText(typed));
        Assert.True(vm.IsDirty);
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);

        Assert.True(condition());
    }

    [Fact]
    public async Task Save_WritesTheEditsToTheFile_AndReopensOverIt()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            var result = await vm.SaveAsync(origin, SiblingFileReplacer.ForCurrentPlatform(), progress: null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.Saved, result.Outcome);
            Assert.Equal("hello, world", File.ReadAllText(path));
            Assert.False(vm.IsDirty);
            Assert.False(vm.HasUnsavedChanges);
            Assert.True(vm.Editor is null or { IsDirty: false, CanUndo: false }, "the edits and their undo history are retired");
            Assert.Equal("hello, world", DocumentText(vm));
            Assert.Same(origin, vm.Origin);
            Assert.Empty(Stages());

            // Edit mode comes back once the fresh scan allows it, with the caret where it was.
            await vm.IndexingTask;
            await WaitUntilAsync(() => vm.IsEditing);
            await WaitUntilAsync(() => vm.Caret!.Caret.Offset == 6);
            Assert.True(vm.CanSave);

            // And the reopened document edits like any other.
            Assert.True(vm.TypeText("!"));
            Assert.Equal("hello,! world", DocumentText(vm));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SaveAs_WritesANewFile_LeavesTheOriginalAlone_AndReadsFromTheNewOne()
    {
        string path = WriteFile("hello world");
        string copyPath = Path.Combine(tempDir, "copy.txt");
        var vm = await EditedAsync(new FileByteOrigin(path), 0, ">");
        try
        {
            var destination = new FileByteOrigin(copyPath);
            var result = await vm.SaveAsync(destination, SiblingFileReplacer.ForCurrentPlatform(), progress: null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.Saved, result.Outcome);
            Assert.Equal(">hello world", File.ReadAllText(copyPath));
            Assert.Equal("hello world", File.ReadAllText(path));
            Assert.Same(destination, vm.Origin);
            Assert.Equal(copyPath, vm.FilePath);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SaveAs_FromAPaste_GivesTheDocumentAFile()
    {
        var paste = new MemoryByteOrigin(Encoding.UTF8.GetBytes("pasted"), "Pasted text");
        string path = Path.Combine(tempDir, "pasted.txt");
        var vm = await EditedAsync(paste, 6, " text");
        try
        {
            var result = await vm.SaveAsync(new FileByteOrigin(path), SiblingFileReplacer.ForCurrentPlatform(), progress: null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.Saved, result.Outcome);
            Assert.Equal("pasted text", File.ReadAllText(path));
            Assert.Equal(path, vm.Origin!.Path);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AFailedCommit_LeavesTheFileUntouched_AndTheEditsOpenOverIt()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            var result = await vm.SaveAsync(origin, new FailingCommitReplacer(), progress: null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.NotSaved, result.Outcome);
            Assert.Contains("Your edits are still open", result.Message);
            Assert.Equal("hello world", File.ReadAllText(path));
            Assert.True(vm.IsDirty);
            Assert.False(vm.IsSaving);
            Assert.Equal("hello, world", DocumentText(vm));
            Assert.Empty(Stages());

            // Remapped, not merely readable: editing and undo carry on over the same edits.
            Assert.True(vm.TypeText("!"));
            Assert.Equal("hello,! world", DocumentText(vm));
            // One typing run on either side of the failed save, so one undo takes both.
            Assert.True(vm.Undo());
            Assert.Equal("hello world", DocumentText(vm));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ACommitThatLostTheFile_KeepsWhatWasBeingSaved()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            var replacer = new FailingCommitReplacer(beforeFailing: () => File.Delete(path));
            var result = await vm.SaveAsync(origin, replacer, progress: null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.NotSavedAndDocumentLost, result.Outcome);
            string stage = Assert.Single(Stages());
            Assert.Contains(stage, result.Message);
            Assert.Equal("hello, world", File.ReadAllText(stage));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AStageThatCannotBeCreated_ChangesNothing()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            var result = await vm.SaveAsync(origin, new RefusingReplacer(), progress: null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.NotSaved, result.Outcome);
            Assert.Contains("disk is full", result.Message);
            Assert.True(vm.IsDirty);
            Assert.False(vm.IsSaving);
            Assert.True(vm.CanSave);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task EditsArePaused_WhileTheCopyRuns()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            var replacer = new GatedReplacer();
            var saving = vm.SaveAsync(origin, replacer, progress: null, CancellationToken.None);
            replacer.WriteStarted.Wait(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsSaving);
            Assert.False(vm.CanSave);
            Assert.True(vm.TypeText("x"), "the key is swallowed rather than passed on");
            Assert.True(vm.DeleteBackward());
            Assert.Equal("hello, world", DocumentText(vm));

            replacer.Release.Set();
            var result = await saving;

            Assert.Equal(DocumentSaveOutcome.Saved, result.Outcome);
            Assert.Equal("hello, world", File.ReadAllText(path));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ClosingTheDocumentMidSave_StopsTheCopy_AndLeavesTheFile()
    {
        // Several write chunks, so the copy reaches a cancellation check while it is slowed down.
        string content = new string('x', ByteSourceReading.WriteChunkBytes * 3);
        string path = WriteFile(content);
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 0, "y");

        var replacer = new GatedReplacer { SlowWrites = true };
        replacer.Release.Set();
        var saving = vm.SaveAsync(origin, replacer, progress: null, CancellationToken.None);
        replacer.WriteStarted.Wait(TimeSpan.FromSeconds(5));

        vm.Dispose();
        var result = await saving;

        Assert.Equal(DocumentSaveOutcome.NotSaved, result.Outcome);
        Assert.Equal(content.Length, new FileInfo(path).Length);
        Assert.Empty(Stages());
    }

    /// <summary>Everything in <paramref name="directory"/> except the settings folder, so a test
    /// can show a stopped save left no file of any name behind.</summary>
    private static string[] FilesIn(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    /// <summary>Several write chunks, so a stop lands between two of them.</summary>
    private string WriteLargeFile() => WriteFile(new string('x', ByteSourceReading.WriteChunkBytes * 3));

    [Fact]
    public async Task StoppingASaveMidCopy_LeavesNothingOnDisk_AndTheDocumentStillEdited()
    {
        string path = WriteLargeFile();
        byte[] original = File.ReadAllBytes(path);
        string[] filesBefore = FilesIn(tempDir);
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 0, "edited:");
        try
        {
            using var stopping = new CancellationTokenSource();
            var replacer = new GatedReplacer { SlowWrites = true };
            replacer.Release.Set();
            var saving = vm.SaveAsync(origin, replacer, progress: null, stopping.Token);
            replacer.WriteStarted.Wait(TimeSpan.FromSeconds(5));

            stopping.Cancel();
            var result = await saving;

            Assert.Equal(DocumentSaveOutcome.Stopped, result.Outcome);
            Assert.Equal(filesBefore, FilesIn(tempDir));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(vm.IsDirty);
            Assert.True(vm.HasUnsavedChanges);
            Assert.StartsWith("edited:xxx", DocumentText(vm));
            Assert.False(vm.IsSaving);
            Assert.True(vm.CanSave);

            // Editing carries on, and a save after the stopped one goes through.
            Assert.True(vm.TypeText("!"));
            var retry = await vm.SaveAsync(origin, SiblingFileReplacer.ForCurrentPlatform(), progress: null, CancellationToken.None);
            Assert.Equal(DocumentSaveOutcome.Saved, retry.Outcome);
            Assert.StartsWith("edited:!xxx", File.ReadAllText(path));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task StoppingASaveAs_LeavesNoNewFile()
    {
        string path = WriteLargeFile();
        string copyPath = Path.Combine(tempDir, "copy.txt");
        string[] filesBefore = FilesIn(tempDir);
        var origin = new FileByteOrigin(path);
        var vm = await EditedAsync(origin, 0, "edited:");
        try
        {
            using var stopping = new CancellationTokenSource();
            var replacer = new GatedReplacer { SlowWrites = true };
            replacer.Release.Set();
            var saving = vm.SaveAsync(new FileByteOrigin(copyPath), replacer, progress: null, stopping.Token);
            replacer.WriteStarted.Wait(TimeSpan.FromSeconds(5));

            stopping.Cancel();
            var result = await saving;

            Assert.Equal(DocumentSaveOutcome.Stopped, result.Outcome);
            Assert.False(File.Exists(copyPath));
            Assert.Equal(filesBefore, FilesIn(tempDir));
            Assert.Same(origin, vm.Origin);
            Assert.True(vm.IsDirty);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>The stop arrives after the last byte is written but before the swap - the one
    /// moment the copy cannot notice it. The swap must still not happen.</summary>
    [Fact]
    public async Task AStopThatArrivesAsTheCopyFinishes_StillPreventsTheSwap()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        string[] filesBefore = FilesIn(tempDir);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            using var stopping = new CancellationTokenSource();
            var result = await vm.SaveAsync(origin, new StopOnSealReplacer(stopping), progress: null, stopping.Token);

            Assert.Equal(DocumentSaveOutcome.Stopped, result.Outcome);
            Assert.Equal("hello world", File.ReadAllText(path));
            Assert.Equal(filesBefore, FilesIn(tempDir));
            Assert.Equal("hello, world", DocumentText(vm));
            Assert.True(vm.IsDirty);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ASaveStoppedBeforeItStarts_WritesNothing()
    {
        string path = WriteFile("hello world");
        var origin = new FileByteOrigin(path);
        string[] filesBefore = FilesIn(tempDir);
        var vm = await EditedAsync(origin, 5, ",");
        try
        {
            var result = await vm.SaveAsync(origin, SiblingFileReplacer.ForCurrentPlatform(), progress: null,
                new CancellationToken(canceled: true));

            Assert.Equal(DocumentSaveOutcome.Stopped, result.Outcome);
            Assert.Equal(filesBefore, FilesIn(tempDir));
            Assert.Equal("hello world", File.ReadAllText(path));
            Assert.True(vm.IsDirty);
            Assert.False(vm.IsSaving);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>A real stage that requests the stop from inside <see cref="StagedFile.Seal"/>,
    /// which runs on the copy's thread after its last write and last cancellation check.</summary>
    private sealed class StopOnSealReplacer(CancellationTokenSource stopping) : IFileReplacer
    {
        public StagedFile Stage(IByteOrigin destination, long contentLength) =>
            new Wrapper(SiblingFileReplacer.ForCurrentPlatform().Stage(destination, contentLength), stopping);

        private sealed class Wrapper(StagedFile inner, CancellationTokenSource stopping) : StagedFile
        {
            public override Stream Content => inner.Content;
            public override string Location => inner.Location;
            public override void Commit() => inner.Commit();
            public override void KeepStagedContent() => inner.KeepStagedContent();
            public override void Dispose() => inner.Dispose();

            public override void Seal()
            {
                inner.Seal();
                stopping.Cancel();
            }
        }
    }

    /// <summary>Stages for real, then fails the swap - after optionally doing something to the
    /// destination first, the way a half-completed Windows ReplaceFile can.</summary>
    private sealed class FailingCommitReplacer(Action? beforeFailing = null) : IFileReplacer
    {
        public StagedFile Stage(IByteOrigin destination, long contentLength) =>
            new Wrapper(SiblingFileReplacer.ForCurrentPlatform().Stage(destination, contentLength), beforeFailing);

        private sealed class Wrapper(StagedFile inner, Action? beforeFailing) : StagedFile
        {
            public override Stream Content => inner.Content;
            public override string Location => inner.Location;
            public override void Seal() => inner.Seal();
            public override void KeepStagedContent() => inner.KeepStagedContent();
            public override void Dispose() => inner.Dispose();

            public override void Commit()
            {
                inner.Seal();
                beforeFailing?.Invoke();
                throw new IOException("The file is in use by another process.");
            }
        }
    }

    private sealed class RefusingReplacer : IFileReplacer
    {
        public StagedFile Stage(IByteOrigin destination, long contentLength) =>
            throw new IOException("The disk is full.");
    }

    /// <summary>A real stage whose content stream holds the first write until released, so a
    /// test can act while the copy is in flight.</summary>
    private sealed class GatedReplacer : IFileReplacer
    {
        public ManualResetEventSlim WriteStarted { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        /// <summary>Makes every write take a while, so a cancellation lands between two.</summary>
        public bool SlowWrites { get; init; }

        public StagedFile Stage(IByteOrigin destination, long contentLength) =>
            new Wrapper(SiblingFileReplacer.ForCurrentPlatform().Stage(destination, contentLength), this);

        private sealed class Wrapper(StagedFile inner, GatedReplacer gate) : StagedFile
        {
            private readonly GatedStream content = new(inner.Content, gate);

            public override Stream Content => content;
            public override string Location => inner.Location;
            public override void Seal() => inner.Seal();
            public override void Commit() => inner.Commit();
            public override void KeepStagedContent() => inner.KeepStagedContent();
            public override void Dispose() => inner.Dispose();
        }

        private sealed class GatedStream(Stream inner, GatedReplacer gate) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                gate.WriteStarted.Set();
                gate.Release.Wait(TimeSpan.FromSeconds(10));
                if (gate.SlowWrites)
                    Thread.Sleep(100);

                inner.Write(buffer);
            }
        }
    }
}
