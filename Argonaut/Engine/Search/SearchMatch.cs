namespace Argonaut.Engine.Search;

/// <summary>One search hit: absolute byte offset in the file and the matched byte length.</summary>
public readonly record struct SearchMatch(long Offset, int Length);
