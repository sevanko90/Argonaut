using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Saving;
using Argonaut.Features.Raw;
using Argonaut.Ui.Documents;

namespace Argonaut.Tests.Features.Raw;

/// <summary>
/// The raw editor writes line breaks the way the file already does - or the platform's way when
/// it has no convention - and converts a file's line endings on its way into the saved file.
/// </summary>
public sealed class RawLineEndingTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("argonaut-raw-line-endings-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public async Task Enter_InsertsTheFilesLineEnding()
    {
        var vm = await EditingAsync(new MemoryByteOrigin("a\r\nb\r\n"u8.ToArray(), "doc.txt"), LineEndingStyle.Lf);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(0, 2, 0, 6, true));
            vm.Caret!.PlaceAt(1);

            Assert.True(vm.InsertNewLine());

            Assert.Equal("a\r\n\r\nb\r\n", DocumentText(vm));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Enter_InAMixedFile_InsertsTheMostCommonEnding()
    {
        var vm = await EditingAsync(new MemoryByteOrigin("a\nb\nc\r\n"u8.ToArray(), "doc.txt"), LineEndingStyle.CrLf);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(2, 1, 0, 7, true));
            vm.Caret!.PlaceAt(1);

            vm.InsertNewLine();

            Assert.Equal("a\n\nb\nc\r\n", DocumentText(vm));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Theory]
    [InlineData(LineEndingStyle.CrLf, "\r\n")]
    [InlineData(LineEndingStyle.Lf, "\n")]
    public async Task Enter_InADocumentWithNoBreaks_InsertsThePlatformsEnding(LineEndingStyle platform, string expected)
    {
        var vm = await EditingAsync(new MemoryByteOrigin([], "new"), platform);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(0, 0, 0, 0, true));

            vm.InsertNewLine();

            Assert.Equal(expected, DocumentText(vm));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task APaste_TakesTheFilesLineEnding()
    {
        var vm = await EditingAsync(new MemoryByteOrigin("x\n"u8.ToArray(), "doc.txt"), LineEndingStyle.CrLf);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(1, 0, 0, 2, true));
            vm.Caret!.PlaceAt(2);

            Assert.True(vm.Paste("one\r\ntwo\r\n"u8));

            Assert.Equal("x\none\ntwo\n", DocumentText(vm));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AConversion_IsAnUnsavedChange_AndChoosingTheFilesOwnEndingCancelsIt()
    {
        var vm = await LoadedAsync(new MemoryByteOrigin("a\nb\n"u8.ToArray(), "doc.txt"));
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(2, 0, 0, 4, true));
            Assert.False(vm.CanConvertLineEndings); // read-only to the user until edit mode is on
            vm.ConvertLineEndingsOnSave(LineEndingStyle.CrLf);
            Assert.Null(vm.PendingLineEnding);

            vm.SetEditing(true);
            Assert.True(vm.CanConvertLineEndings);

            vm.ConvertLineEndingsOnSave(LineEndingStyle.CrLf);
            Assert.Equal(LineEndingStyle.CrLf, vm.PendingLineEnding);
            Assert.True(vm.HasUnsavedChanges);
            Assert.False(vm.IsDirty);
            Assert.Contains("line endings → CRLF on save", vm.StatusDetail);

            vm.ConvertLineEndingsOnSave(LineEndingStyle.Lf);
            Assert.Null(vm.PendingLineEnding);
            Assert.False(vm.HasUnsavedChanges);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Saving_WritesEveryBreakAsTheTarget_AndTheCaretFollowsItsText()
    {
        string path = WriteFile("one\ntwo\nthree");
        var origin = new FileByteOrigin(path);
        var vm = await EditingAsync(origin, LineEndingStyle.Lf);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(2, 0, 0, 13, true));
            vm.Caret!.PlaceAt(8); // the 't' of "three"
            vm.ConvertLineEndingsOnSave(LineEndingStyle.CrLf);

            var result = await vm.SaveAsync(origin, SiblingFileReplacer.ForCurrentPlatform(), null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.Saved, result.Outcome);
            Assert.Equal("one\r\ntwo\r\nthree", File.ReadAllText(path));
            Assert.Null(vm.PendingLineEnding);
            Assert.False(vm.HasUnsavedChanges);
            await vm.IndexingTask;
            await WaitUntilAsync(() => vm.Caret!.Caret.Offset == 10);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Saving_ConvertsTheEditsToo()
    {
        string path = WriteFile("a\r\nb\r\n");
        var origin = new FileByteOrigin(path);
        var vm = await EditingAsync(origin, LineEndingStyle.CrLf);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(0, 2, 0, 6, true));
            vm.Caret!.PlaceAt(6);
            vm.Paste("c\nd"u8);
            vm.ConvertLineEndingsOnSave(LineEndingStyle.Lf);

            await vm.SaveAsync(origin, SiblingFileReplacer.ForCurrentPlatform(), null, CancellationToken.None);

            Assert.Equal("a\nb\nc\nd", File.ReadAllText(path));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AFailedCommit_KeepsTheConversionPending_AndTheFileUntouched()
    {
        string path = WriteFile("a\nb\n");
        var origin = new FileByteOrigin(path);
        var vm = await EditingAsync(origin, LineEndingStyle.Lf);
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(2, 0, 0, 4, true));
            vm.ConvertLineEndingsOnSave(LineEndingStyle.CrLf);

            var result = await vm.SaveAsync(origin, new FailingCommitReplacer(), null, CancellationToken.None);

            Assert.Equal(DocumentSaveOutcome.NotSaved, result.Outcome);
            Assert.Equal("a\nb\n", File.ReadAllText(path));
            Assert.Equal(LineEndingStyle.CrLf, vm.PendingLineEnding);
            Assert.True(vm.HasUnsavedChanges);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AStoppedSave_KeepsTheConversionPending()
    {
        string path = WriteFile("a\nb\n");
        var origin = new FileByteOrigin(path);
        var vm = await EditingAsync(origin, LineEndingStyle.Lf);
        using var stopping = new CancellationTokenSource();
        try
        {
            vm.UseInputLineEndings(new LineEndingTally(2, 0, 0, 4, true));
            vm.ConvertLineEndingsOnSave(LineEndingStyle.CrLf);
            stopping.Cancel();

            var result = await vm.SaveAsync(origin, SiblingFileReplacer.ForCurrentPlatform(), null, stopping.Token);

            Assert.Equal(DocumentSaveOutcome.Stopped, result.Outcome);
            Assert.Equal(LineEndingStyle.CrLf, vm.PendingLineEnding);
            Assert.Equal("a\nb\n", File.ReadAllText(path));
        }
        finally
        {
            vm.Dispose();
        }
    }

    private string WriteFile(string content)
    {
        string path = Path.Combine(tempDir, "doc.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task<RawViewModel> LoadedAsync(IByteOrigin origin, LineEndingStyle platform = LineEndingStyle.Lf)
    {
        var vm = new RawViewModel(new RawViewSettings(), defaultLineEnding: platform);
        await vm.LoadAsync(origin);
        await vm.IndexingTask;
        return vm;
    }

    private static async Task<RawViewModel> EditingAsync(IByteOrigin origin, LineEndingStyle platform)
    {
        var vm = await LoadedAsync(origin, platform);
        vm.SetEditing(true);
        Assert.True(vm.IsEditing);
        return vm;
    }

    private static string DocumentText(RawViewModel vm)
    {
        var source = vm.Document!;
        var bytes = new byte[source.AvailableLength];
        source.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);

        Assert.True(condition());
    }

    private sealed class FailingCommitReplacer : IFileReplacer
    {
        public StagedFile Stage(IByteOrigin destination, long contentLength) =>
            new Wrapper(SiblingFileReplacer.ForCurrentPlatform().Stage(destination, contentLength));

        private sealed class Wrapper(StagedFile inner) : StagedFile
        {
            public override Stream Content => inner.Content;
            public override string Location => inner.Location;
            public override void Seal() => inner.Seal();
            public override void KeepStagedContent() => inner.KeepStagedContent();
            public override void Dispose() => inner.Dispose();
            public override void Commit() => throw new IOException("The file is in use by another process.");
        }
    }
}
