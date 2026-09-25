using Argonaut.Features.Raw;

namespace Argonaut.Tests.Features.Raw;

/// <summary>
/// Exercises the raw toolbar's wrap-width combo in isolation: index↔width mapping, the
/// ComboBox-teardown guard, and the apply callback. Remembering the choice is the document's job
/// (see RawViewModelTests).
/// </summary>
public sealed class RawToolbarViewModelTests
{
    [Fact]
    public void Ctor_SeedsIndexFromTheInitialWidth()
    {
        Assert.Equal(0, new RawToolbarViewModel(80, _ => { }, _ => { }).WrapWidthIndex);
        Assert.Equal(1, new RawToolbarViewModel(160, _ => { }, _ => { }).WrapWidthIndex);
        Assert.Equal(2, new RawToolbarViewModel(512, _ => { }, _ => { }).WrapWidthIndex);
    }

    [Fact]
    public void Ctor_UnknownWidth_FallsBackToTheDefault()
    {
        var toolbar = new RawToolbarViewModel(999, _ => { }, _ => { });
        Assert.Equal(Array.IndexOf(RawViewSettings.Widths, RawViewSettings.DefaultWrapWidth), toolbar.WrapWidthIndex);
    }

    [Fact]
    public void WrapWidthIndex_Set_InvokesCallback()
    {
        var applied = new List<int>();
        var toolbar = new RawToolbarViewModel(160, applied.Add, _ => { });

        toolbar.WrapWidthIndex = 2;

        Assert.Equal(new[] { 512 }, applied);

        // Reassigning the same index is a no-op via the SetField equality guard.
        toolbar.WrapWidthIndex = 2;
        Assert.Equal(new[] { 512 }, applied);
    }

    [Fact]
    public void OutOfRangeIndexAssignments_AreIgnored()
    {
        var applied = new List<int>();
        var toolbar = new RawToolbarViewModel(160, applied.Add, _ => { });

        toolbar.WrapWidthIndex = -1; // a ComboBox raises -1 during teardown
        toolbar.WrapWidthIndex = RawViewSettings.Widths.Length;

        Assert.Equal(1, toolbar.WrapWidthIndex);
        Assert.Empty(applied);
    }
}
