using System;
using System.Collections.Generic;
using System.Globalization;
using Argonaut.Engine.Detection;

namespace Argonaut.Shell.CommandLine;

/// <summary>Lines <see cref="First"/>..<see cref="Last"/> (1-based, inclusive) asked for on the
/// command line; <see cref="Last"/> is null for a range that runs to the end of the file.</summary>
public readonly record struct LineSpan(long First, long? Last);

/// <summary>
/// What the command line asked for: the files to open, and optionally a run of lines to open as
/// an excerpt, a view to force, or the usage text. Parsed once at startup, before any window
/// exists - <see cref="ShowHelp"/> is acted on before Avalonia starts at all.
///
/// An option it does not know is ignored rather than rejected: the OS adds its own (older macOS
/// passes <c>-psn_…</c> on a Finder launch), and refusing to open the file over one would be
/// worse than not honouring it. A known option with a bad value is an <see cref="Error"/>.
/// </summary>
public sealed class StartupArguments
{
    private StartupArguments(IReadOnlyList<string> paths, LineSpan? lines, FileTypeDetector.FileKind? view, bool showHelp, string? error)
    {
        Paths = paths;
        Lines = lines;
        View = view;
        ShowHelp = showHelp;
        Error = error;
    }

    /// <summary>The positional arguments, in order: one file, or two to compare.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary><c>--lines</c>: open just these lines of the file.</summary>
    public LineSpan? Lines { get; }

    /// <summary><c>--view</c>: open in this view instead of the one detection picks.</summary>
    public FileTypeDetector.FileKind? View { get; }

    /// <summary><c>--help</c>: print the usage text and exit without opening a window.</summary>
    public bool ShowHelp { get; }

    /// <summary>What was wrong with the command line, or null when nothing was.</summary>
    public string? Error { get; }

    /// <summary>The <c>--view</c> names, each with the view it opens.</summary>
    public static IReadOnlyList<(string Name, FileTypeDetector.FileKind Kind)> ViewNames { get; } =
    [
        ("json", FileTypeDetector.FileKind.Json),
        ("ndjson", FileTypeDetector.FileKind.Ndjson),
        ("csv", FileTypeDetector.FileKind.Csv),
        ("tsv", FileTypeDetector.FileKind.Tsv),
        ("raw", FileTypeDetector.FileKind.Unidentified),
    ];

    public static StartupArguments Parse(IReadOnlyList<string> args) => Parse(args, OperatingSystem.IsWindows());

    /// <param name="windowsSwitches">Whether <c>/?</c> means help. Only on Windows: elsewhere a
    /// leading slash is an absolute path, and a file can be called anything.</param>
    public static StartupArguments Parse(IReadOnlyList<string> args, bool windowsSwitches)
    {
        var paths = new List<string>();
        LineSpan? lines = null;
        FileTypeDetector.FileKind? view = null;
        bool showHelp = false;
        string? error = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "--help" or "-h" || (windowsSwitches && arg == "/?"))
            {
                showHelp = true;
                continue;
            }

            if (!arg.StartsWith('-'))
            {
                paths.Add(arg);
                continue;
            }

            var (name, inlineValue) = SplitOption(arg);
            if (name is not ("--lines" or "--view"))
                continue;

            string? value = inlineValue ?? (i + 1 < args.Count ? args[++i] : null);
            if (value is null)
            {
                error ??= $"{name} needs a value.";
                continue;
            }

            if (name == "--lines")
            {
                lines = ParseLines(value);
                if (lines is null)
                    error ??= $"--lines: \"{value}\" is not a line range. Use A-B, A-, A+COUNT or A.";
            }
            else
            {
                view = ParseView(value);
                if (view is null)
                    error ??= $"--view: \"{value}\" is not a view. Use one of: {string.Join(", ", ViewNamesOnly())}.";
            }
        }

        if (error is null && paths.Count > 1 && lines is not null)
            error = "--lines opens one file, not a comparison.";

        if (error is null && (lines is not null || view is not null) && paths.Count == 0)
            error = "--lines and --view need a file to open.";

        return new StartupArguments(paths, lines, view, showHelp, error);
    }

    /// <summary>
    /// <c>A-B</c>, <c>A-</c> (to the end), <c>A+COUNT</c> or a single line <c>A</c>, where each
    /// number may carry a <c>k</c> or <c>m</c> multiplier - <c>1m-2m</c>, <c>2.5m+500k</c>.
    /// </summary>
    internal static LineSpan? ParseLines(string text)
    {
        text = text.Trim();
        int plus = text.IndexOf('+');
        int dash = text.IndexOf('-');

        if (plus > 0)
        {
            return ParseCount(text[..plus]) is { } first && ParseCount(text[(plus + 1)..]) is { } count && first >= 1 && count >= 1
                ? new LineSpan(first, first + count - 1)
                : null;
        }

        if (dash > 0)
        {
            if (ParseCount(text[..dash]) is not { } first || first < 1)
                return null;

            string rest = text[(dash + 1)..];
            if (rest.Length == 0)
                return new LineSpan(first, null);

            return ParseCount(rest) is { } last && last >= first ? new LineSpan(first, last) : null;
        }

        return ParseCount(text) is { } single && single >= 1 ? new LineSpan(single, single) : null;
    }

    /// <summary>A whole number with an optional <c>k</c> (thousand) or <c>m</c> (million)
    /// multiplier, which may follow a decimal - <c>2.5m</c>. Null when it is not one.</summary>
    internal static long? ParseCount(string text)
    {
        text = text.Trim();
        long multiplier = 1;
        if (text.EndsWith('k') || text.EndsWith('K'))
            multiplier = 1_000;
        else if (text.EndsWith('m') || text.EndsWith('M'))
            multiplier = 1_000_000;

        string digits = multiplier == 1 ? text : text[..^1];
        if (multiplier == 1)
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long whole) ? whole : null;

        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal scaled))
            return null;

        decimal value = scaled * multiplier;
        return value == decimal.Truncate(value) && value <= long.MaxValue ? (long)value : null;
    }

    private static FileTypeDetector.FileKind? ParseView(string text)
    {
        foreach (var (name, kind) in ViewNames)
        {
            if (string.Equals(name, text.Trim(), StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return null;
    }

    private static IEnumerable<string> ViewNamesOnly()
    {
        foreach (var (name, _) in ViewNames)
            yield return name;
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        int equals = arg.IndexOf('=');
        return equals < 0 ? (arg, null) : (arg[..equals], arg[(equals + 1)..]);
    }
}
