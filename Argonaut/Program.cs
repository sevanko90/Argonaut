using Avalonia;
using Avalonia.Media;
using System;
using Argonaut.Infrastructure.Updates;

namespace Argonaut;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else touches Avalonia: the GitHub build's updater intercepts
        // launches made by its own install/update lifecycle (see VelopackAppUpdater).
        AppUpdaters.Current.OnProcessStart();

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
