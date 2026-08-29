using Argonaut.Features.Search;

namespace Argonaut.Tests;

/// <summary>
/// Every shape the find bar's status line can take, asserted as exact strings - these are user
/// visible, and previously could only be exercised by running a real scan to the state that
/// produced them (a capped search needs a million matches).
/// </summary>
public class FindStatusTextTests
{
    private static string Compose(int stopCount, int position, string? stopUnit = null,
        bool scansComplete = true, bool hitCap = false, bool allFailedToOpen = false, bool wrapped = false)
        => FindStatusText.Compose(stopCount, position, stopUnit, scansComplete, hitCap, allFailedToOpen, wrapped);

    [Fact]
    public void NothingFoundYet_WhileScanning_Searching()
        => Assert.Equal("Searching…", Compose(stopCount: 0, position: 0, scansComplete: false));

    [Fact]
    public void NothingFound_WhenComplete_NoMatches()
        => Assert.Equal("No matches", Compose(stopCount: 0, position: 0));

    /// <summary>"No matches" would be a lie when the file was never read at all.</summary>
    [Fact]
    public void NothingFound_WhenEveryScanFailedToOpen_SearchFailed()
        => Assert.Equal("Search failed", Compose(stopCount: 0, position: 0, allFailedToOpen: true));

    [Fact]
    public void SelectedStop_ReadsNOfM()
        => Assert.Equal("3 of 47", Compose(stopCount: 47, position: 3));

    [Fact]
    public void SelectedStop_WithAUnit_QualifiesTheCount()
        => Assert.Equal("3 of 47 rows", Compose(stopCount: 47, position: 3, stopUnit: "rows"));

    /// <summary>No selection yet (or the selected stop was collapsed away by a fold): report the
    /// total alone.</summary>
    [Fact]
    public void NoSelection_ReportsTheTotalOnly()
    {
        Assert.Equal("47 matches", Compose(stopCount: 47, position: 0));
        Assert.Equal("47 rows", Compose(stopCount: 47, position: 0, stopUnit: "rows"));
    }

    [Fact]
    public void OneResult_IsNotPluralized()
    {
        Assert.Equal("1 match", Compose(stopCount: 1, position: 0));
        Assert.Equal("1 row", Compose(stopCount: 1, position: 0, stopUnit: "rows"));
    }

    [Fact]
    public void StillScanning_AppendsTheQualifier()
        => Assert.Equal("3 of 47 (searching…)", Compose(stopCount: 47, position: 3, scansComplete: false));

    [Fact]
    public void AtTheMatchCap_SaysSoOnceComplete()
        => Assert.Equal("3 of 47 (first 47 only)", Compose(stopCount: 47, position: 3, hitCap: true));

    /// <summary>The cap qualifier belongs to a finished search; while scanning, "(searching…)"
    /// wins - the count is still moving, so "first n only" would be premature.</summary>
    [Fact]
    public void AtTheMatchCap_WhileScanning_ReportsSearchingInstead()
        => Assert.Equal("3 of 47 (searching…)",
            Compose(stopCount: 47, position: 3, scansComplete: false, hitCap: true));

    [Fact]
    public void Wrapped_IsAppendedLast()
    {
        Assert.Equal("1 of 47 — wrapped", Compose(stopCount: 47, position: 1, wrapped: true));
        Assert.Equal("1 of 47 (first 47 only) — wrapped",
            Compose(stopCount: 47, position: 1, hitCap: true, wrapped: true));
    }

    [Fact]
    public void LargeCounts_AreGroupedForReading()
        => Assert.Equal("1,234 of 1,000,000", Compose(stopCount: 1_000_000, position: 1_234));
}
