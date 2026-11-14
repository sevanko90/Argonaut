namespace Argonaut.Engine.Bytes;

/// <summary>How an origin is described to a person.</summary>
public static class ByteOriginText
{
    /// <summary>
    /// Where the document is, as text - the full path or URL - or its display name when it has
    /// no locator. Display text only: what touches the file system reads the
    /// <see cref="FileLocator"/>, never this.
    /// </summary>
    public static string LocationText(this IByteOrigin origin) => origin.Locator?.DisplayText ?? origin.DisplayName;
}
