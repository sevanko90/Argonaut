namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// What a span of a raw row is, for colour. <see cref="Plain"/> is never emitted: text no span
/// covers is plain.
/// </summary>
public enum RawTextStyle : byte
{
    Plain,
    Key,
    Section,
    String,
    Number,
    Keyword,
    Literal,
    Punctuation,
    Comment,
}
