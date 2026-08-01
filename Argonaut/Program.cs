using Avalonia;
using Avalonia.Media;
using System;
using Velopack;

namespace Argonaut;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else touches Avalonia: handles a pending install/update
        // completion (e.g. Windows relaunching post-update) and then returns normally on a
        // regular launch.
        // AutoApplyOnStartup is ON by default, meaning every launch silently swaps in
        // whatever's the highest-versioned .nupkg sitting in Velopack's local package cache
        // (~/Library/Caches/velopack/<app>/packages on macOS) - not just updates staged by
        // our own UpdateService. That cache accumulates across every local packaging run, so
        // a local dev build with a lower version than a previously packed one gets silently
        // replaced on launch with no dialog. UpdateService.ApplyUpdatesAndRestart still applies
        // updates explicitly (after user confirmation) regardless of this setting - only the
        // implicit on-startup swap is disabled.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Inter (embedded via WithInterFont) is the default UI chrome font; content
            // surfaces override with AppContentFontFamily explicitly in XAML.
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
