using Argonaut.Engine.Bytes;

namespace Argonaut.Tests.Support;

internal static class OriginFilePath
{
    /// <summary>The file an origin is, or null when it is not a file - what tests assert where
    /// they used to read the origin's path.</summary>
    public static string? FilePathOf(this IByteOrigin origin) => (origin.Locator as FileLocator)?.Path;
}
