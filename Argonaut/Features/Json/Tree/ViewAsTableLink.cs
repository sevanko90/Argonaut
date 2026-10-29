namespace Argonaut.Features.Json.Tree;

/// <summary>Show this array's elements as a table - what a JSON tree row's "View as table" action
/// carries as its <c>TreeRun.Link</c>.</summary>
/// <param name="ArrayStart">The array's opening bracket.</param>
public sealed record ViewAsTableLink(long ArrayStart);
