using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Exercises the raw toolbar's wrap-width combo in isolation: index↔width mapping, the
/// ComboBox-teardown guard, and persistence + apply-callback ordering. AppDataPaths.RootOverride
/// redirects the preference store to a temp dir so the developer's real settings are never touched.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawToolbarViewModelTests : IDisposable
{
    private readonly string settingsRoot;

    public RawToolbarViewModelTests()
    {
        settingsRoot = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
        AppDataPaths.RootOverride = settingsRoot;
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
        try { if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void Ctor_SeedsIndexFromTheInitialWidth()
    {
        Assert.Equal(0, new RawToolbarViewModel(80, _ => { }).WrapWidthIndex);
        Assert.Equal(1, new RawToolbarViewModel(160, _ => { }).WrapWidthIndex);
        Assert.Equal(2, new RawToolbarViewModel(512, _ => { }).WrapWidthIndex);
    }

    [Fact]
    public void Ctor_UnknownWidth_FallsBackToTheDefault()
    {
        var toolbar = new RawToolbarViewModel(999, _ => { });
        Assert.Equal(Array.IndexOf(RawWrapWidthPreference.Widths, RawWrapWidthPreference.Default), toolbar.WrapWidthIndex);
    }

    [Fact]
    public void WrapWidthIndex_Set_PersistsAndInvokesCallback()
    {
        var applied = new List<int>();
        var toolbar = new RawToolbarViewModel(160, applied.Add);

        toolbar.WrapWidthIndex = 2;

        Assert.Equal(512, RawWrapWidthPreference.Load());
        Assert.Equal(new[] { 512 }, applied);

        // Reassigning the same index is a no-op via the SetField equality guard.
        toolbar.WrapWidthIndex = 2;
        Assert.Equal(new[] { 512 }, applied);
    }

    [Fact]
    public void OutOfRangeIndexAssignments_AreIgnored()
    {
        var applied = new List<int>();
        var toolbar = new RawToolbarViewModel(160, applied.Add);

        toolbar.WrapWidthIndex = -1; // a ComboBox raises -1 during teardown
        toolbar.WrapWidthIndex = RawWrapWidthPreference.Widths.Length;

        Assert.Equal(1, toolbar.WrapWidthIndex);
        Assert.Empty(applied);
    }
}
