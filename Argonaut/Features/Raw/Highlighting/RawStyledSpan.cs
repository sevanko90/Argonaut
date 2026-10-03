namespace Argonaut.Features.Raw.Highlighting;

/// <summary>One coloured span of a row's drawn text, measured in chars.</summary>
public readonly record struct RawStyledSpan(int Start, int Length, RawTextStyle Style);
