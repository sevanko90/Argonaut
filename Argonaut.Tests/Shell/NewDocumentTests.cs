using System.Text;
using Argonaut.Engine.Settings;
using Argonaut.Features.Raw;
using Argonaut.Shell;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Shell;

/// <summary>
/// Documents the user makes rather than opens - a new empty one, or a paste - open ready to
/// type into; anything opened from somewhere starts read-only.
/// </summary>
public class NewDocumentTests
{
    private static MainWindowViewModel Shell(string? clipboard = null) =>
        new(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            readClipboardBytes: () => Task.FromResult(clipboard is null ? null : Encoding.UTF8.GetBytes(clipboard)));

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);

        return condition();
    }

    [Fact]
    public async Task ANewDocument_IsEmptyRawText_InEditMode()
    {
        var shell = Shell();

        await shell.NewDocumentAsync();

        var raw = Assert.IsType<RawViewModel>(shell.CurrentDocument);
        Assert.Null(raw.Origin!.Path);
        Assert.Equal("Untitled", raw.Origin.DisplayName);
        Assert.Equal(0, raw.Origin.AvailableLength);
        Assert.True(await EventuallyAsync(() => raw.IsEditing));
    }

    [Fact]
    public async Task ANewDocument_CanBeTypedInto()
    {
        var shell = Shell();
        await shell.NewDocumentAsync();
        var raw = Assert.IsType<RawViewModel>(shell.CurrentDocument);
        Assert.True(await EventuallyAsync(() => raw.IsEditing));

        Assert.True(raw.TypeText("hello"));

        var bytes = new byte[raw.Document!.AvailableLength];
        raw.Document.CopyTo(0, bytes);
        Assert.Equal("hello", Encoding.UTF8.GetString(bytes));
        Assert.True(raw.IsDirty);
    }

    [Fact]
    public async Task APasteOfPlainText_OpensInEditMode()
    {
        var shell = Shell("a@x.com\nb@y.com\n");

        await shell.PasteAsync();

        var raw = Assert.IsType<RawViewModel>(shell.CurrentDocument);
        Assert.True(raw.Origin!.StartsEditable);
        Assert.True(await EventuallyAsync(() => raw.IsEditing));
    }

    [Fact]
    public async Task DecodedBytesFromAHint_StartReadOnly()
    {
        var shell = Shell();

        await shell.OpenBytesAsync(Encoding.UTF8.GetBytes("decoded text\n"), "Decoded", "the decoded value");

        var raw = Assert.IsType<RawViewModel>(shell.CurrentDocument);
        Assert.False(raw.Origin!.StartsEditable);
        await raw.IndexingTask;
        await Task.Delay(50);
        Assert.False(raw.IsEditing);
    }
}
