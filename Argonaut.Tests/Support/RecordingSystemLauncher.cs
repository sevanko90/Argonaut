using Argonaut.Ui.Launching;

namespace Argonaut.Tests.Support;

/// <summary>An <see cref="ISystemLauncher"/> that records what it was asked to open instead of
/// opening anything.</summary>
internal sealed class RecordingSystemLauncher : ISystemLauncher
{
    public List<string> FoldersShown { get; } = [];

    public List<Uri> AddressesOpened { get; } = [];

    public void ShowFolder(string path) => FoldersShown.Add(path);

    public void OpenAddress(Uri address) => AddressesOpened.Add(address);
}
