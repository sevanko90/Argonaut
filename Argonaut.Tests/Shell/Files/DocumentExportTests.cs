using Argonaut.Engine.Bytes;
using Argonaut.Engine.Http;
using Argonaut.Engine.Logging;
using Argonaut.Engine.Saving;
using Argonaut.Shell.Files;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Shell.Files;

/// <summary>The shell's side of the binary view: the name a copy is offered under, saving it
/// where the user picks, and handing a staged file to the system's app for it.</summary>
public sealed class DocumentExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public DocumentExportTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private HttpResponseOrigin PngResponse()
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".response");
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G']);
        HttpRequestSpec.TryCreate(HttpRequestMethod.Get, "https://img.test/logo", null, out var request, out _);
        return new HttpResponseOrigin(new HttpLocator(request!), path, "image/png");
    }

    private DocumentExport Export(Func<string, Task<string?>> pick, RecordingSystemLauncher launcher)
        => new(new OriginExporter(Path.Combine(root, "Opened")), pick, launcher, NullDiagnosticLog.Instance);

    [Fact]
    public void AFile_IsOfferedUnderItsOwnName_AndAnythingElseWithItsMediaTypesExtension()
    {
        string path = Path.Combine(root, "data.bin");
        File.WriteAllBytes(path, [1]);
        using var file = new FileByteOrigin(path);
        using var response = PngResponse();
        using var paste = new MemoryByteOrigin([1], "Pasted text");

        Assert.Equal("data.bin", DocumentExport.SuggestedFileName(file));
        Assert.Equal("logo · img.test.png", DocumentExport.SuggestedFileName(response));
        Assert.Equal("Pasted text.txt", DocumentExport.SuggestedFileName(paste));
    }

    [Fact]
    public async Task SaveCopy_WritesWhereTheUserPicked()
    {
        string? offered = null;
        string destination = Path.Combine(root, "saved.png");
        using var response = PngResponse();

        await Export(name => { offered = name; return Task.FromResult<string?>(destination); }, new()).SaveCopyAsync(response);

        Assert.Equal("logo · img.test.png", offered);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task SaveCopy_Cancelled_WritesNothing()
    {
        using var response = PngResponse();

        await Export(_ => Task.FromResult<string?>(null), new()).SaveCopyAsync(response);

        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public async Task OpenInDefaultApp_HandsTheSystemAFileNamedForItsKind()
    {
        var launcher = new RecordingSystemLauncher();
        using var response = PngResponse();

        await Export(_ => Task.FromResult<string?>(null), launcher).OpenInDefaultAppAsync(response);

        string opened = Assert.Single(launcher.FilesOpened);
        Assert.Equal("logo · img.test.png", Path.GetFileName(opened));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(opened));
    }
}
