namespace Argonaut.Tests.Support;

/// <summary>Paths to the checked-in fixture files, which the project copies beside the test
/// assembly under the same relative folders they live in.</summary>
internal static class Fixtures
{
    public static string UnicodeNamesAndValuesJson =>
        Path.Combine(AppContext.BaseDirectory, "Features", "Json", "Fixtures", "unicode-names-and-values.json");
}
