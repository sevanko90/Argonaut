using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Argonaut.Ui.Icons;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;

namespace Argonaut.Tests.Ui.Icons;

/// <summary>
/// The icon dictionary is generated from Lucide's SVGs, so these hold it to what LucideIcon
/// assumes: every entry is one geometry on the 24x24 grid, and every key the app's markup asks
/// for exists - a missing <c>StaticResource</c> only fails when its view is first built.
/// </summary>
public sealed partial class LucideIconsTests
{
    [Fact]
    public Task EveryIcon_IsAGeometryOnTheLucideGrid()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LucideIconsTests).Assembly);
        return session.Dispatch(() =>
        {
            var icons = LoadIcons();
            Assert.NotEmpty(icons);

            foreach (var (key, resource) in icons)
            {
                var geometry = Assert.IsAssignableFrom<Geometry>(resource);
                var bounds = geometry.Bounds;
                Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{key} is empty");
                Assert.True(bounds.X >= -0.01 && bounds.Y >= -0.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
                    $"{key} spills off the 24x24 grid: {bounds}");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task EveryIconTheMarkupNames_Exists()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LucideIconsTests).Assembly);
        return session.Dispatch(() =>
        {
            var icons = LoadIcons();
            var sources = Directory.EnumerateFiles(AppSourceRoot(), "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".axaml") || path.EndsWith(".cs"))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

            int references = 0;
            foreach (string path in sources)
            {
                foreach (Match match in IconKey().Matches(File.ReadAllText(path)))
                {
                    references++;
                    Assert.True(icons.ContainsKey(match.Groups[1].Value),
                        $"{Path.GetFileName(path)} names {match.Groups[1].Value}, which LucideIcons.axaml does not define");
                }
            }

            Assert.True(references > 0);
        }, CancellationToken.None);
    }

    [Fact]
    public Task Icon_MeasuresToItsSize()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LucideIconsTests).Assembly);
        return session.Dispatch(() =>
        {
            var icon = new LucideIcon { Size = 13 };
            icon.Measure(Size.Infinity);
            Assert.Equal(new Size(13, 13), icon.DesiredSize);
        }, CancellationToken.None);
    }

    private static Dictionary<string, object?> LoadIcons()
    {
        var include = new ResourceInclude(new Uri("avares://Argonaut/"))
        {
            Source = new Uri("avares://Argonaut/Ui/Icons/LucideIcons.axaml"),
        };
        var dictionary = Assert.IsAssignableFrom<IResourceDictionary>(include.Loaded);
        return dictionary.Keys.OfType<string>().ToDictionary(key => key, key => dictionary[key]);
    }

    private static string AppSourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "Argonaut"));

    /// <summary>A key as markup names it (<c>{StaticResource Icon.Edit}</c>) or as code looks it
    /// up (<c>"Icon.ThemeLight"</c>).</summary>
    [GeneratedRegex("(?:StaticResource |\")(Icon\\.[A-Za-z]+)")]
    private static partial Regex IconKey();
}
