namespace Argonaut.Ui.Tree;

/// <summary>Where a path leads: the target row's start, or why it leads nowhere.</summary>
public readonly record struct TreePathResult(long? Target, string? Error);
