using System;
using System.Collections.Generic;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Selection;
using Argonaut.Engine.Text;

namespace Argonaut.Shell.CommandLine;

/// <summary>Lines <see cref="First"/>..<see cref="Last"/> (1-based, inclusive) asked for on the
/// command line; <see cref="Last"/> is null for a range that runs to the end of the file.</summary>
public readonly record struct LineSpan(long First, long? Last);

/// <summary>Bytes <see cref="First"/>..<see cref="Last"/> asked for on the command line -
/// zero-based and inclusive, as an HTTP range names them; <see cref="Last"/> is null for a range
/// that runs to the end of the file.</summary>
public readonly record struct ByteSpan(long First, long? Last);

/// <summary>
/// What the command line asked for: the files to open, and optionally a run of lines to open as
/// an excerpt, a filter file of many of them, a view to force, or the usage text. Parsed once at startup, before any window
/// exists - <see cref="ShowHelp"/> is acted on before Avalonia starts at all.
///
/// An option it does not know is ignored rather than rejected: the OS adds its own (older macOS
/// passes <c>-psn_…</c> on a Finder launch), and refusing to open the file over one would be
/// worse than not honouring it. A known option with a bad value is an <see cref="Error"/>.
/// </summary>
public sealed class StartupArguments
{
    private StartupArguments(IReadOnlyList<string> paths, LineSpan? lines, ByteSpan? bytes, string? filterPath, SelectionUnit? filterUnit,
        FileTypeDetector.FileKind? view, bool showHelp, string? error)
    {
        Paths = paths;
        Lines = lines;
        Bytes = bytes;
        FilterPath = filterPath;
        FilterUnit = filterUnit;
        View = view;
        ShowHelp = showHelp;
        Error = error;
    }

    /// <summary>The positional arguments, in order: one file, or two to compare.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary><c>--lines</c>: open just these lines of the file.</summary>
    public LineSpan? Lines { get; }

    /// <summary><c>--bytes</c>: open just these bytes of the file - for a file whose lines are
    /// too long to ask for by line.</summary>
    public ByteSpan? Bytes { get; }

    /// <summary><c>--filter</c>: a file listing the lines or bytes of the file to open, as one
    /// read-only view of all of them.</summary>
    public string? FilterPath { get; }

    /// <summary>What <see cref="FilterPath"/>'s entries count - lines with <c>--lines</c>, bytes
    /// with <c>--bytes</c>; null without a filter.</summary>
    public SelectionUnit? FilterUnit { get; }

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
        ByteSpan? bytes = null;
        bool linesAsked = false;
        bool bytesAsked = false;
        string? filterPath = null;
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
            if (name is not ("--lines" or "--bytes" or "--filter" or "--view"))
                continue;

            // --lines and --bytes take their range from --filter when they have none of their
            // own, so the next argument is theirs only if it is not another option. A range
            // never starts with '-'.
            bool rangeOption = name is "--lines" or "--bytes";
            linesAsked |= name == "--lines";
            bytesAsked |= name == "--bytes";
            string? value = inlineValue
                ?? (i + 1 < args.Count && !(rangeOption && args[i + 1].StartsWith('-')) ? args[++i] : null);
            if (value is null)
            {
                if (!rangeOption)
                    error ??= name == "--filter" ? "--filter needs a file." : $"{name} needs a value.";
                continue;
            }

            if (name == "--filter")
            {
                filterPath = value;
            }
            else if (name == "--lines")
            {
                lines = ParseLines(value);
                if (lines is null)
                    error ??= $"--lines: \"{value}\" is not a line range. Use A-B, A-, A+COUNT or A.";
            }
            else if (name == "--bytes")
            {
                bytes = ParseBytes(value);
                if (bytes is null)
                    error ??= $"--bytes: \"{value}\" is not a byte range. Use A-B, A- or A+COUNT.";
            }
            else
            {
                view = ParseView(value);
                if (view is null)
                    error ??= $"--view: \"{value}\" is not a view. Use one of: {string.Join(", ", ViewNamesOnly())}.";
            }
        }

        string unitName = linesAsked ? "--lines" : "--bytes";
        bool rangeGiven = lines is not null || bytes is not null;
        bool anyAsked = linesAsked || bytesAsked || filterPath is not null || view is not null;

        if (error is null && linesAsked && bytesAsked)
            error = "Use --lines or --bytes, not both.";

        if (error is null && filterPath is not null && !linesAsked && !bytesAsked)
            error = "--filter needs --lines or --bytes, to say whether the file lists lines or bytes.";

        if (error is null && filterPath is not null && rangeGiven)
            error = $"Give {unitName} a range or --filter, not both.";

        if (error is null && (linesAsked || bytesAsked) && !rangeGiven && filterPath is null)
            error = $"{unitName} needs a range, or --filter with a file of them.";

        if (error is null && filterPath is not null && view == FileTypeDetector.FileKind.Json)
            error = "--view json cannot show a selection of lines. Use ndjson, csv, tsv or raw.";

        if (error is null && paths.Count > 1 && anyAsked)
            error = $"{(linesAsked || bytesAsked ? unitName : "--view")} opens one file, not a comparison.";

        if (error is null && anyAsked && paths.Count == 0)
            error = "--lines, --bytes, --filter and --view need a file to open.";

        SelectionUnit? filterUnit = filterPath is null ? null : linesAsked ? SelectionUnit.Lines : SelectionUnit.Bytes;
        return new StartupArguments(paths, lines, bytes, filterPath, filterUnit, view, showHelp, error);
    }

    /// <summary>
    /// <c>A-B</c>, <c>A-</c> (to the end), <c>A+COUNT</c> or a single line <c>A</c>, where each
    /// number may carry a <c>k</c> or <c>m</c> multiplier - <c>1m-2m</c>, <c>2.5m+500k</c>.
    /// </summary>
    internal static LineSpan? ParseLines(string text)
        => RangeSyntax.ParseRange(text, lowest: 1, allowSingle: true) is { } range ? new LineSpan(range.First, range.Last) : null;

    /// <summary>
    /// <c>A-B</c>, <c>A-</c> (to the end) or <c>A+COUNT</c>, zero-based, where each number may
    /// carry a <c>k</c>, <c>m</c> or <c>g</c> multiplier or be hex (<c>0x1F400</c>). No single
    /// byte: one byte is not a document anyone opens on purpose, and <c>--bytes 5</c> is more
    /// likely a mistyped count.
    /// </summary>
    internal static ByteSpan? ParseBytes(string text)
        => RangeSyntax.ParseRange(text, lowest: 0, allowSingle: false) is { } range ? new ByteSpan(range.First, range.Last) : null;

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
