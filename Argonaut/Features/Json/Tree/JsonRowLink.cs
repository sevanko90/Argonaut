namespace Argonaut.Features.Json.Tree;

/// <summary>What a clickable run on a JSON tree row does, carried as the run's
/// <c>TreeRun.Link</c> and handed back by the surface when it is clicked.</summary>
public abstract record JsonRowLink;

/// <summary>Open the raw view at a value too long to show in full.</summary>
/// <param name="Offset">The value's content offset in the document.</param>
public sealed record ViewInRawLink(long Offset) : JsonRowLink;

/// <summary>Change how the date hint on this value is decoded.</summary>
/// <param name="ValueOffset">The value's start - the key its override is stored under.</param>
public sealed record DateSchemeLink(long ValueOffset) : JsonRowLink;

/// <summary>Show this array's elements as a table.</summary>
/// <param name="ArrayStart">The array's opening bracket.</param>
public sealed record ViewAsTableLink(long ArrayStart) : JsonRowLink;
