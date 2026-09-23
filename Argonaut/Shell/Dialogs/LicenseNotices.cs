using System;
using System.Collections.Generic;
using System.IO;

namespace Argonaut.Shell.Dialogs;

/// <summary>One component's licence and copyright notice, as shown in the notices window.</summary>
/// <param name="Component">What the notice is for, e.g. "Avalonia".</param>
/// <param name="Homepage">Where the component lives, or null when the section names none.</param>
/// <param name="Packages">The NuGet packages the notice covers with their versions, or null.</param>
/// <param name="NoticeText">The licence and copyright text, verbatim.</param>
public sealed record LicenseNotice(string Component, string? Homepage, string? Packages, string NoticeText);

/// <summary>
/// Argonaut's own licence plus the third-party notices for everything it ships, read from the
/// embedded LICENSE and THIRD-PARTY-NOTICES.txt. Loaded only when the notices window opens, never
/// at startup; the combined text is a few hundred KB, held only while the window is.
/// </summary>
public static class LicenseNotices
{
    internal const string AppLicenseResourceName = "Argonaut.LICENSE";
    internal const string ThirdPartyResourceName = "Argonaut.THIRD-PARTY-NOTICES.txt";

    // The section fence scripts/make-third-party-notices.py writes. A fence only opens a section
    // when a "Component:" line follows it, so rules inside the notice texts themselves never do.
    private static readonly string SectionRule = new('=', 80);
    private const string ComponentKey = "Component: ";
    private const string HomepageKey = "Homepage: ";
    private const string PackagesKey = "Packages: ";

    /// <summary>Argonaut's own licence first, then each third-party component in file order.</summary>
    public static IReadOnlyList<LicenseNotice> Load()
    {
        var notices = new List<LicenseNotice>
        {
            new(AppInfo.Name, AppInfo.RepoUrl, null, ReadResource(AppLicenseResourceName).Trim()),
        };
        notices.AddRange(Parse(ReadResource(ThirdPartyResourceName)));
        return notices;
    }

    /// <summary>Splits a notices file into its sections. Text before the first section is ignored.</summary>
    internal static IReadOnlyList<LicenseNotice> Parse(string notices)
    {
        string[] lines = notices.ReplaceLineEndings("\n").Split('\n');
        var sections = new List<LicenseNotice>();

        int i = NextSectionStart(lines, 0);
        while (i < lines.Length)
        {
            // Header: key lines between the opening fence and the closing one.
            string component = "", homepage = "", packages = "";
            int line = i + 1;
            for (; line < lines.Length && lines[line] != SectionRule; line++)
            {
                if (lines[line].StartsWith(ComponentKey, StringComparison.Ordinal))
                    component = lines[line][ComponentKey.Length..].Trim();
                else if (lines[line].StartsWith(HomepageKey, StringComparison.Ordinal))
                    homepage = lines[line][HomepageKey.Length..].Trim();
                else if (lines[line].StartsWith(PackagesKey, StringComparison.Ordinal))
                    packages = lines[line][PackagesKey.Length..].Trim();
            }

            int bodyStart = line + 1;
            int next = NextSectionStart(lines, bodyStart);
            string noticeText = string.Join('\n', lines, Math.Min(bodyStart, lines.Length),
                Math.Max(0, next - bodyStart)).Trim('\n');

            sections.Add(new LicenseNotice(component, NullIfEmpty(homepage), NullIfEmpty(packages), noticeText));
            i = next;
        }

        return sections;
    }

    private static int NextSectionStart(string[] lines, int from)
    {
        for (int i = from; i < lines.Length - 1; i++)
        {
            if (lines[i] == SectionRule && lines[i + 1].StartsWith(ComponentKey, StringComparison.Ordinal))
                return i;
        }

        return lines.Length;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static string ReadResource(string resourceName)
    {
        using var resource = typeof(LicenseNotices).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {resourceName}.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
