using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut;

public partial class App : Application
{
    private MainWindow? mainWindow;

    // macOS re-signals each CLI-launched path as its own IActivatableLifetime.Activated /
    // FileActivatedEventArgs on top of argv - one event per path, fired moments after this
    // process starts. Left unfiltered, those duplicate events race the argv-driven open in
    // MainWindowViewModel (both sides bump openRequestId / currentFilePath concurrently) and
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
        // TODO(temporary diagnostics): remove OpenDebugLog once "Open With" file loading is confirmed working.
        OpenDebugLog.Write($"OnFrameworkInitializationCompleted: ApplicationLifetime={ApplicationLifetime?.GetType().Name}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            mainWindow = window;
            desktop.MainWindow = window;

            OpenDebugLog.Write($"desktop.Args = [{string.Join(", ", desktop.Args ?? [])}]");

            // Up to two positional paths: `argonaut a.json b.json` opens a diff when both are
            // JSON (see MainWindowViewModel.OpenPathsAsync); a third or later positional arg is
            // ignored.
            var positionalArgs = desktop.Args?.Where(a => !a.StartsWith('-')).ToArray() ?? [];
            if (positionalArgs.Length > 0)
            {
                var first = positionalArgs[0];
                var second = positionalArgs.Length > 1 ? positionalArgs[1] : null;
                OpenDebugLog.Write($"Opening from Args: first={first}, second={second}");
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
        OpenDebugLog.Write($"TryGetFeature<IActivatableLifetime> = {activatable?.GetType().FullName ?? "<null>"}");
        if (activatable is not null)
        {
            activatable.Activated += OnActivated;
            OpenDebugLog.Write("Subscribed to IActivatableLifetime.Activated");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        OpenDebugLog.Write($"OnActivated: kind={e.GetType().Name}");

        if (mainWindow is null || e is not FileActivatedEventArgs fileArgs)
            return;

        OpenDebugLog.Write($"FileActivatedEventArgs.Files.Count = {fileArgs.Files.Count}");

        var path = fileArgs.Files.FirstOrDefault()?.TryGetLocalPath();
        OpenDebugLog.Write($"Resolved local path: {path ?? "<null>"}");

        if (path is null)
            return;

        if (startupArgPaths.Remove(Path.GetFullPath(path)))
        {
            OpenDebugLog.Write($"OnActivated: ignoring duplicate of startup arg '{path}'");
            return;
        }

        _ = mainWindow.OpenInitialFileAsync(path);
    }
}