using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Settings;
using Argonaut.Features.Json;
using Argonaut.Features.Raw;
using Argonaut.Shell;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Shell;

/// <summary>A switch to the text view and back through the real view models the catalog builds -
/// no fakes between the shell and the documents.</summary>
public sealed class TextViewHopEndToEndTests
{
    [Fact]
    public async Task SwitchToTextAndBack_CarriesTheCaret()
    {
        var sb = new StringBuilder("{\"items\": [\n");
        for (int i = 0; i < 3000; i++)
            sb.Append(i == 0 ? "" : ",\n").Append($"  {{\"id\": {i}, \"name\": \"item{i}\"}}");
        sb.Append("\n]}\n");
        string json = sb.ToString();
        string path = Path.Combine(Path.GetTempPath(), $"hop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        var shell = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true));
        try
        {
            await shell.OpenPathAsync(path);
            var jsonDoc = Assert.IsType<JsonViewModel>(shell.CurrentDocument);
            await jsonDoc.IndexingTask;

            await shell.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);
            var raw = Assert.IsType<RawViewModel>(shell.CurrentDocument);
            await raw.IndexingTask;

            long target = json.IndexOf("\"item2500\"", StringComparison.Ordinal) + 3;
            await raw.JumpToByteOffsetAsync(target);
            Assert.Equal(ByteRange.At(target), raw.SelectedByteRange);

            await shell.SwitchViewAsync(FileTypeDetector.FileKind.Json);
            var back = Assert.IsType<JsonViewModel>(shell.CurrentDocument);

            // The first JSON view's structure was kept, so this one opens complete and the caret is
            // ready to show at once; the view then resolves it to a row
            // (JsonByteRangeNavigationTests).
            Assert.True(back.IndexingTask.IsCompletedSuccessfully);
            Assert.Equal(target, back.PendingReveal);
        }
        finally
        {
            await shell.CloseFileAsync();
            File.Delete(path);
        }
    }
}
