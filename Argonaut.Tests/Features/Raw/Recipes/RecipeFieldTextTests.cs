using Argonaut.Features.Raw.Recipes;

namespace Argonaut.Tests.Features.Raw.Recipes;

public class RecipeFieldTextTests
{
    [Theory]
    [InlineData(",\n", @",\n")]
    [InlineData("\t'\r\n", @"\t'\r\n")]
    [InlineData(@"c:\temp", @"c:\\temp")]
    [InlineData("{line}", "{line}")]
    public void Display_RoundTrips(string text, string shown)
    {
        Assert.Equal(shown, RecipeFieldText.ToDisplay(text));
        Assert.Equal(text, RecipeFieldText.FromDisplay(shown));
    }

    [Theory]
    [InlineData(@"\d+", @"\d+")]
    [InlineData(@"ends\", @"ends\")]
    public void ABackslashBeforeAnythingElse_IsKeptAsTyped(string shown, string text)
    {
        Assert.Equal(text, RecipeFieldText.FromDisplay(shown));
    }
}
