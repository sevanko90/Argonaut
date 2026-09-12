using System.Text;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Tests;

/// <summary>
/// Opening the clipboard as a document. The clipboard itself is a delegate on the view model, so
/// these drive the whole flow - read, size check, confirm, detect, publish - with no real
/// clipboard and no Avalonia.
/// </summary>
public class ClipboardPasteTests
{
    private static MainWindowViewModel WithClipboard(string? text,
        Func<string, Task<bool>>? confirmReplace = null)
        => new(confirmReplace ?? (_ => Task.FromResult(true)),
            readClipboardText: () => Task.FromResult(text));

    [Fact]
    public async Task PastedJsonOpensAsADocumentWithNoPath()
    {
        var vm = WithClipboard("{\"a\":1,\"b\":[2,3]}");

        await vm.PasteAsync();

        Assert.True(vm.IsFileOpen);
        Assert.NotNull(vm.CurrentDocument);
        Assert.NotNull(vm.CurrentDocument!.Origin);
        Assert.Null(vm.CurrentDocument.Origin!.Path);
        Assert.Equal("Pasted text", vm.CurrentDocument.Origin.DisplayName);
    }

    /// <summary>Detection runs on the bytes, not on a file extension - which a paste has none of.</summary>
    [Theory]
    [InlineData("{\"a\":1}", FileTypeDetector.FileKind.Json)]
    [InlineData("{\"a\":1}\n{\"a\":2}\n", FileTypeDetector.FileKind.Ndjson)]
    [InlineData("a,b,c\n1,2,3\n", FileTypeDetector.FileKind.Csv)]
    public async Task PastedContentIsDetectedByItsBytes(string text, FileTypeDetector.FileKind expected)
    {
        FileTypeDetector.FileKind? detected = null;
        var vm = new MainWindowViewModel(_ => Task.FromResult(true),
            readClipboardText: () => Task.FromResult<string?>(text),
            documentLoader: (kind, origin, _) =>
            {
                detected = kind;
                return Task.FromResult<IDocumentViewModel>(new PasteDocument(origin));
            });

        await vm.PasteAsync();

        Assert.Equal(expected, detected);
    }

    [Fact]
    public async Task APastedDocumentIsNotAddedToRecentFiles()
    {
        var before = vmRecentCount(WithClipboard(null));
        var vm = WithClipboard("{\"a\":1}");

        await vm.PasteAsync();

        Assert.Equal(before, vm.RecentFiles.Count);

        static int vmRecentCount(MainWindowViewModel vm) => vm.RecentFiles.Count;
    }

    [Fact]
    public async Task AnEmptyClipboardOpensNothing()
    {
        var vm = WithClipboard("");

        await vm.PasteAsync();

        Assert.False(vm.IsFileOpen);
    }

    [Fact]
    public async Task NoClipboardReaderMeansPasteIsUnavailableRatherThanAnError()
    {
        var vm = new MainWindowViewModel(_ => Task.FromResult(true));

        Assert.False(vm.CanPaste);
        await vm.PasteAsync();
        Assert.False(vm.IsFileOpen);
    }

    [Fact]
    public async Task AClipboardThatThrowsIsReportedRatherThanPropagated()
    {
        var vm = new MainWindowViewModel(_ => Task.FromResult(true),
            readClipboardText: () => throw new InvalidOperationException("no clipboard today"));

        await vm.PasteAsync();

        Assert.False(vm.IsFileOpen);
    }

    /// <summary>
    /// Past the cap the paste is declined rather than spilled to a temp file - see
    /// MainWindowViewModel.PasteAsync for why spilling would not help.
    /// </summary>
    [Fact]
    public async Task APasteOverTheCapIsDeclined()
    {
        string tooBig = new('x', (int)MainWindowViewModel.MaxPasteBytes + 1);
        var vm = WithClipboard(tooBig);

        await vm.PasteAsync();

        Assert.False(vm.IsFileOpen);
    }

    [Fact]
    public async Task ReplacingAnOpenDocumentAsksFirst_AndDeclineKeepsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"paste-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"fromFile\":true}");
        try
        {
            int asked = 0;
            var vm = new MainWindowViewModel(
                _ => { asked++; return Task.FromResult(false); },
                readClipboardText: () => Task.FromResult<string?>("{\"fromClipboard\":true}"),
                documentLoader: (_, origin, _) => Task.FromResult<IDocumentViewModel>(new PasteDocument(origin)));

            await vm.OpenPathAsync(path);
            var opened = vm.CurrentDocument;

            await vm.PasteAsync();

            Assert.Equal(1, asked);
            Assert.Same(opened, vm.CurrentDocument);
            Assert.Equal(Path.GetFullPath(path), vm.CurrentDocument!.Origin!.Path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PastedBytesAreTheClipboardTextAsUtf8()
    {
        const string text = "{\"caf\\u00e9\":\"naïve\"}";
        IByteOrigin? captured = null;
        var vm = new MainWindowViewModel(_ => Task.FromResult(true),
            readClipboardText: () => Task.FromResult<string?>(text),
            documentLoader: (_, origin, _) =>
            {
                captured = origin;
                return Task.FromResult<IDocumentViewModel>(new PasteDocument(origin));
            });

        await vm.PasteAsync();

        Assert.NotNull(captured);
        var expected = Encoding.UTF8.GetBytes(text);
        Assert.Equal(expected.Length, captured!.AvailableLength);

        var source = captured.Open();
        try
        {
            Assert.Equal(text, source.GetUtf8String(0, expected.Length));
        }
        finally
        {
            source.Release();
        }
    }

    /// <summary>Minimal document that just carries its origin, so these tests assert on the
    /// origin the shell built rather than on real indexing.</summary>
    private sealed class PasteDocument : ObservableObject, IDocumentViewModel
    {
        public PasteDocument(IByteOrigin origin) => Origin = origin;

        public IByteOrigin? Origin { get; }

        public string FilePath => Origin?.Path ?? Origin?.DisplayName ?? string.Empty;

        public string StatusText => "loaded";

        public IndexFailure? IndexFailure => null;

        public Task IndexingTask => Task.CompletedTask;

        public object? Toolbar => null;

        public ISearchNavigator? CreateSearchNavigator() => null;

        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;

        public void Dispose() { }
    }
}
