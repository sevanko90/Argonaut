namespace Argonaut.Tests.Support;

/// <summary>
/// Addresses for the hint-link tests. The address is the document's own text, so only the
/// schemes a document may launch get through; a <c>file:</c> or custom scheme must never reach
/// the system.
/// </summary>
internal static class HintAddresses
{
    public static TheoryData<string> Safe => new()
    {
        "https://example.com/a?b=c",
        "http://example.com/",
        "mailto:someone@example.com",
    };

    public static TheoryData<string> Unsafe => new()
    {
        "file:///etc/passwd",
        "javascript:alert(1)",
        "vscode://open?file=x",
        "ftp://example.com/file",
    };
}
