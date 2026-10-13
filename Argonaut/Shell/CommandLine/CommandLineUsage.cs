using System;

namespace Argonaut.Shell.CommandLine;

/// <summary>
/// The command-line reference: what <c>--help</c> prints and what the About box shows, from this
/// one text so the two cannot drift apart.
/// </summary>
public static class CommandLineUsage
{
    /// <summary>The options and examples, without the usage line - what the About box shows
    /// under its own heading.</summary>
    public static string Options { get; } = string.Join(Environment.NewLine,
        "  --lines RANGE   Open only these lines of the file, as a document of their own.",
        "                  Memory and indexing time follow the range, not the file.",
        "                  RANGE is A-B, A- (to the end), A+COUNT, or a single line A.",
        "                  Numbers are 1-based and may use k or m: 1m-2m, 2.5m+500k.",
        $"  --view VIEW     Open in this view instead of the detected one: {ViewList()}.",
        "  --help, -h      Print this text and exit." + (OperatingSystem.IsWindows() ? " Also /?." : string.Empty),
        "",
        "Give two files to compare them, when both are JSON.",
        "",
        "Examples:",
        $"  {AppInfo.Name} big.ndjson --lines 1m-2m",
        $"  {AppInfo.Name} export.csv --lines 500k+1000 --view raw",
        $"  {AppInfo.Name} before.json after.json");

    /// <summary>Everything <c>--help</c> prints.</summary>
    public static string Text { get; } = string.Join(Environment.NewLine,
        $"{AppInfo.Name} {AppInfo.Version}",
        "",
        $"Usage: {AppInfo.Name} [FILE [FILE2]] [--lines RANGE] [--view VIEW]",
        "",
        Options);

    private static string ViewList()
    {
        var names = new string[StartupArguments.ViewNames.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = StartupArguments.ViewNames[i].Name;

        return string.Join(", ", names);
    }
}
