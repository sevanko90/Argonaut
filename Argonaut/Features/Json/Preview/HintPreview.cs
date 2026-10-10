using Argonaut.Features.Json.Hints;

namespace Argonaut.Features.Json.Preview;

/// <summary>What a hint opened into: the preview to show, and the decoded bytes it shows, which
/// can be opened as a document of their own - null when nothing was decoded.</summary>
public sealed record HintPreview(ValuePreview Preview, ExpandedValue? Decoded);
