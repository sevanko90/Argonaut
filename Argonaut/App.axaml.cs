using System;
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

            var filePath = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-'));
            if (filePath is not null)
            {
                OpenDebugLog.Write($"Opening from Args: {filePath}");
                _ = window.OpenInitialFileAsync(filePath);
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

        if (path is not null)
            _ = mainWindow.OpenInitialFileAsync(path);
    }
}