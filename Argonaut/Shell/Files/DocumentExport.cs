using System;
using System.IO;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Http;
using Argonaut.Engine.Logging;
using Argonaut.Engine.Saving;
using Argonaut.Features.Binary;
using Argonaut.Ui.Launching;
using Argonaut.Ui.Notifications;

namespace Argonaut.Shell.Files;

/// <summary>
/// The shell's side of the binary view's two actions: asking where to save a copy, and handing a
/// file to the app the system uses for it. The bytes are moved by the exporter; failures become a
/// toast and a line in the log, never an exception into the view.
/// </summary>
public sealed class DocumentExport(IOriginExporter exporter, Func<string, Task<string?>> pickDestination,
    ISystemLauncher launcher, IDiagnosticLog log) : IBinaryDocumentActions
{
    /// <summary>The name a copy of <paramref name="origin"/> is offered under: a file's own name,
    /// otherwise its name with the extension its media type implies (<c>.txt</c> without one).</summary>
    public static string SuggestedFileName(IByteOrigin origin)
        => origin.Locator is FileLocator ? origin.DisplayName : ResponseFileNames.Suggest(origin.DisplayName, origin.MediaType);

    public async Task SaveCopyAsync(IByteOrigin origin)
    {
        string? destination = await pickDestination(SuggestedFileName(origin));
        if (destination is null)
            return;

        try
        {
            await exporter.CopyAsync(origin, destination);
            ToastService.Show($"Saved {Path.GetFileName(destination)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Save copy: writing {origin.DisplayName} to {destination} failed: {ex}");
            ToastService.Show($"Couldn't save a copy: {ex.Message}");
        }
    }

    public async Task OpenInDefaultAppAsync(IByteOrigin origin)
    {
        try
        {
            launcher.OpenFile(await exporter.PrepareToOpenAsync(origin, SuggestedFileName(origin)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Open in default app: staging {origin.DisplayName} failed: {ex}");
            ToastService.Show($"Couldn't open it in another app: {ex.Message}");
        }
    }
}
