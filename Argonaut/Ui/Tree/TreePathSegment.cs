namespace Argonaut.Ui.Tree;

/// <summary>One clickable piece of a path - <c>$</c>, <c>.nested</c>, <c>[2]</c> - and the
/// byte offset of the row it names, which revealing it lands on.</summary>
public readonly record struct TreePathSegment(string Label, long Target);
