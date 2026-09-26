namespace Argonaut.Features.Json.Indexing;

/// <summary>
/// What a JSON value is - the format kind the JSON tree's nodes carry
/// (<c>TreeNode.FormatKind</c>). A container is <see cref="StartObject"/> or
/// <see cref="StartArray"/>; the end kinds name a closing bracket.
/// </summary>
public enum JsonTokenKind
{
    StartObject,
    EndObject,
    StartArray,
    EndArray,
    String,
    Number,
    True,
    False,
    Null
}
