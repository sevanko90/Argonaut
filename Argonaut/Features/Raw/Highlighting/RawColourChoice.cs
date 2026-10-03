namespace Argonaut.Features.Raw.Highlighting;

/// <summary>What the raw view's colour picker can ask for. The order is the picker's.</summary>
public enum RawColourChoice
{
    /// <summary>The lexer the file's name or its first lines suggest, if any.</summary>
    Auto,
    Off,
    Json,
    Config,
}
