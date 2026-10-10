using System;

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

/// <summary>Show what a hint's value encodes, in full - a JWT's claims, JSON in a string,
/// Base64's bytes.</summary>
/// <param name="ValueStart">The value's start, its opening quote.</param>
/// <param name="ValueEnd">Just past its closing quote.</param>
/// <param name="Kind">The hint, which says how to decode it.</param>
public sealed record ExpandHintLink(long ValueStart, long ValueEnd, Hints.ValueHintKind Kind) : JsonRowLink;

/// <summary>Open a web or mail address the value holds, in the system's handler for it.</summary>
/// <param name="Address">An absolute <c>http</c>, <c>https</c> or <c>mailto</c> URI - the only
/// schemes a value may hand to the system.</param>
public sealed record OpenUrlLink(Uri Address) : JsonRowLink;
