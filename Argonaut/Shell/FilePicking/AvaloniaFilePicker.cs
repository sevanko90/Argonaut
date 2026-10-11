using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Argonaut.Shell.FilePicking;

/// <summary>The platform's own open dialog through Avalonia. It has no way to show hidden files.</summary>
public sealed class AvaloniaFilePicker : IFilePicker
{
    private readonly TopLevel owner;

    public AvaloniaFilePicker(TopLevel owner) => this.owner = owner;

    public async Task<string?> PickFileAsync(string title, string? startFolder = null, bool showsHiddenFiles = true)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (startFolder is not null)
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);

        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
