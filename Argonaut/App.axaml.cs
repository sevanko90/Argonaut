using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Argonaut.Engine.Logging;
using Argonaut.Engine.Settings;
using Argonaut.Features.Json.Schema;
using Argonaut.Shell;

namespace Argonaut;

public partial class App : Application
{
    private MainWindow? mainWindow;

    // Debug builds log to a file for whoever is debugging them; anything else keeps nothing.
    private readonly IDiagnosticLog log = CreateLog();

    // macOS re-signals each CLI-launched path as its own IActivatableLifetime.Activated /
    // FileActivatedEventArgs on top of argv - one event per path, fired moments after this
    // process starts. Left unfiltered, those duplicate events race the argv-driven open in
    // MainWindowViewModel (both sides bump openRequest / currentFilePath concurrently) and
    // the window never finishes coming up. Paths handled from desktop.Args are recorded here
    // and each is consumed (removed) the first time a matching Activated event arrives, so
    // only genuine later "Open With" activations reach OpenInitialFileAsync.
    private readonly HashSet<string> startupArgPaths = new(StringComparer.OrdinalIgnoreCase);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        log.Write($"OnFrameworkInitializationCompleted: ApplicationLifetime={ApplicationLifetime?.GetType().Name}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The composition root: where every file location and OS action is decided, then
            // handed down to what uses it.
            var settings = SettingsStore.Open(AppDataPaths.SettingsFile);
            desktop.Exit += (_, _) => settings.Save();
            if (log is IDisposable ownedLog)
                desktop.Exit += (_, _) => ownedLog.Dispose();

            var schemaCatalog = new JsonSchemaCatalog(JsonSchemaCatalog.BundledDirectoryBesideApp, AppDataPaths.SchemasDirectory,
                revealDirectory: RevealDirectory);

            var window = new MainWindow(settings, schemaCatalog, log, OpenLogFolderAction());
            mainWindow = window;
            desktop.MainWindow = window;

            log.Write($"desktop.Args = [{string.Join(", ", desktop.Args ?? [])}]");

            // Up to two positional paths: `argonaut a.json b.json` opens a diff when both are
            // JSON (see MainWindowViewModel.OpenPathsAsync); a third or later positional arg is
            // ignored.
            var positionalArgs = desktop.Args?.Where(a => !a.StartsWith('-')).ToArray() ?? [];
            if (positionalArgs.Length > 0)
            {
                var first = positionalArgs[0];
                var second = positionalArgs.Length > 1 ? positionalArgs[1] : null;
                log.Write($"Opening from Args: first={first}, second={second}");
                foreach (var arg in positionalArgs)
                {
                    try { startupArgPaths.Add(Path.GetFullPath(arg)); }
                    catch { /* malformed path - let OpenPathsAsync reject it normally */ }
                }
                _ = window.OpenInitialFileAsync(first, second);
            }
        }

        // macOS launches "Open With" via a document-open activation event rather than argv.
        // IActivatableLifetime is NOT implemented by ClassicDesktopStyleApplicationLifetime
        // (Application.ApplicationLifetime) - it's a separate optional platform feature.
        var activatable = this.TryGetFeature<IActivatableLifetime>();
        log.Write($"TryGetFeature<IActivatableLifetime> = {activatable?.GetType().FullName ?? "<null>"}");
        if (activatable is not null)
        {
            activatable.Activated += OnActivated;
            log.Write("Subscribed to IActivatableLifetime.Activated");
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens <paramref name="path"/> in the platform's file manager.</summary>
    private static void RevealDirectory(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>What the status bar's log-folder button does: Debug builds only, since no other
    /// build writes a log. Creates the folder first, because the log's writer creates it in the
    /// background and may not have got there yet.</summary>
    private static Action? OpenLogFolderAction()
    {
#if DEBUG
        return () =>
        {
            string folder = Path.GetDirectoryName(AppDataPaths.DebugLogFile)!;
            Directory.CreateDirectory(folder);
            RevealDirectory(folder);
        };
#else
        return null;
#endif
    }

    private static IDiagnosticLog CreateLog()
    {
#if DEBUG
        return new FileDiagnosticLog(AppDataPaths.DebugLogFile);
#else
        return NullDiagnosticLog.Instance;
#endif
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        log.Write($"OnActivated: kind={e.GetType().Name}");

        if (mainWindow is null || e is not FileActivatedEventArgs fileArgs)
            return;

        log.Write($"FileActivatedEventArgs.Files.Count = {fileArgs.Files.Count}");

        var path = fileArgs.Files.FirstOrDefault()?.TryGetLocalPath();
        log.Write($"Resolved local path: {path ?? "<null>"}");

        if (path is null)
            return;

        if (startupArgPaths.Remove(Path.GetFullPath(path)))
        {
            log.Write($"OnActivated: ignoring duplicate of startup arg '{path}'");
            return;
        }

        _ = mainWindow.OpenInitialFileAsync(path);
    }
}