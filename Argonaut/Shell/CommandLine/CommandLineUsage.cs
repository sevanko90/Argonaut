using System;

namespace Argonaut.Shell.CommandLine;

/// <summary>
/// The command-line reference: what <c>--help</c> prints and what the About box shows, from this
/// one text so the two cannot drift apart.
/// </summary>
public static class CommandLineUsage
{
    /// <summary>The options and examples, without the usage line.</summary>
    public static string Options { get; } = string.Join(Environment.NewLine,
        "  --lines RANGE   Open only these lines of the file, as a document of their own.",
        "                  Memory and indexing time follow the range, not the file.",
        "                  RANGE is A-B, A- (to the end), A+COUNT, or a single line A.",
        "                  Numbers are 1-based and may use k or m: 1m-2m, 2.5m+500k.",
        "  --bytes RANGE   Open only these bytes of the file, for lines too long to ask for",
        "                  by line. Bytes count from 0 and the last is included: 0-499 is",
        "                  the first 500. RANGE is A-B, A- or A+COUNT; numbers may use",
        "                  k, m or g (thousands, millions, billions) or be hex: 0x1F400.",
        "  --filter FILE   With --lines or --bytes instead of a range: open every line or",
        "                  byte range FILE lists, in file order, as one read-only view.",
        "                  Entries are ranges as above, separated by commas or newlines;",
        "                  # starts a comment. The view follows FILE as it changes.",
        "  --view VIEW     Open in this view instead of the detected one:",
        $"                  {ViewList()}.",
        "  --help, -h      Print this text and exit." + (OperatingSystem.IsWindows() ? " Also /?." : string.Empty),
        "",
        "Give two files to compare them, when both are JSON.",
        "",
        "Examples:",
        $"  {AppInfo.Name} big.ndjson --lines 1m-2m",
        $"  {AppInfo.Name} export.csv --lines 500k+1000 --view raw",
        $"  {AppInfo.Name} minified.json --bytes 1.5g+100m",
        $"  {AppInfo.Name} big.ndjson --lines --filter picks.txt",
        $"  {AppInfo.Name} before.json after.json");

    /// <summary>The usage line and the options - what the About box shows, under its own
    /// name and version.</summary>
    public static string Reference { get; } = string.Join(Environment.NewLine,
        $"Usage: {AppInfo.Name} [FILE [FILE2]] [--lines RANGE | --bytes RANGE | --lines --filter FILE | --bytes --filter FILE] [--view VIEW]",
        "",
        Options);

    /// <summary>Everything <c>--help</c> prints.</summary>
    public static string Text { get; } = string.Join(Environment.NewLine,
        $"{AppInfo.Name} {AppInfo.Version}",
        "",
        Reference);

    private static string ViewList()
    {
        var names = new string[StartupArguments.ViewNames.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = StartupArguments.ViewNames[i].Name;

        return string.Join(", ", names);
    }
}
