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
        => WithClipboardBytes(text is null ? null : Encoding.UTF8.GetBytes(text), confirmReplace);

    private static MainWindowViewModel WithClipboardBytes(byte[]? bytes,
        Func<string, Task<bool>>? confirmReplace = null)
        => new(confirmReplace ?? (_ => Task.FromResult(true)),
            readClipboardBytes: () => Task.FromResult(bytes));

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
            readClipboardBytes: () => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(text)),
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
            readClipboardBytes: () => throw new InvalidOperationException("no clipboard today"));

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
        var vm = WithClipboardBytes(new byte[MainWindowViewModel.MaxPasteBytes + 1]);

        await vm.PasteAsync();

        Assert.False(vm.IsFileOpen);
    }

    /// <summary>
    /// The clipboard hands over UTF-8 bytes when the platform offers such a format, and those
    /// bytes reach the document unaltered - no decode/re-encode round trip on the way.
    /// </summary>
    [Fact]
    public async Task Utf8BytesFromTheClipboardReachTheDocumentUnaltered()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("{\"name\":\"caf\u00e9 na\u00efve \ud83d\ude80\"}");
        IByteOrigin? captured = null;
        var vm = new MainWindowViewModel(_ => Task.FromResult(true),
            readClipboardBytes: () => Task.FromResult<byte[]?>(utf8),
            documentLoader: (_, origin, _) =>
            {
                captured = origin;
                return Task.FromResult<IDocumentViewModel>(new PasteDocument(origin));
            });

        await vm.PasteAsync();

        Assert.NotNull(captured);
        Assert.Equal(utf8.Length, captured!.AvailableLength);

        var source = captured.Open();
        try
        {
            Assert.True(source.RequireContiguous(0, utf8.Length).SequenceEqual(utf8));
        }
        finally
        {
            source.Release();
        }
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
                readClipboardBytes: () => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("{\"fromClipboard\":true}")),
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
