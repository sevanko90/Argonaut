using Argonaut.Engine.Hints;

namespace Argonaut.Ui.Tree;

/// <summary>Show what a hint's value encodes, in full - a JWT's claims, JSON in a string,
/// Base64's bytes.</summary>
/// <param name="ValueStart">The value's first byte, as written in the document.</param>
/// <param name="ValueEnd">Just past its last byte, as written.</param>
/// <param name="Kind">The hint, which says how to decode it.</param>
public sealed record ExpandHintLink(long ValueStart, long ValueEnd, ValueHintKind Kind);
