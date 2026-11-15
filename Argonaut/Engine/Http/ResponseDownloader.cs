using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Progress;

namespace Argonaut.Engine.Http;

/// <summary>
/// Downloads a request's response into the response folder and opens it as an
/// <see cref="HttpResponseOrigin"/>. The body goes straight to a file readable by the user only,
/// reporting progress as bytes against the server's stated length (or a running count when it
/// states none); a download that is stopped or fails leaves nothing behind.
///
/// The folder comes from the composition root - inside the app's own data folder, which the App
/// Store sandbox always allows. The whole response is written before the origin exists: every
/// view reads whole ranges, so a still-growing response would need a mapping pre-sized to its
/// length, which is a later step.
/// </summary>
public sealed class ResponseDownloader(IHttpFetcher fetcher, string folder)
{
    public async Task<HttpResponseOrigin> DownloadAsync(HttpRequestSpec request, IProgressReporter? progress, CancellationToken stopping)
    {
        var locator = new HttpLocator(request);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".response");

        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            string? mediaType;
            await using (var file = new FileStream(path, options))
            {
                string message = $"Downloading {locator.DefaultName}";
                progress?.Report(message);
                mediaType = await fetcher.FetchAsync(request, file, (received, total) => progress?.Report(message, received, total), stopping);
            }

            stopping.ThrowIfCancellationRequested();
            return new HttpResponseOrigin(locator, path, mediaType);
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the response folder's sweep.
            }

            throw;
        }
    }
}
