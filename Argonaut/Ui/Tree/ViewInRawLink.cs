namespace Argonaut.Ui.Tree;

/// <summary>Open the raw view at a value too long to show in full - what a tree row's "Open in
/// raw" action carries as its <c>TreeRun.Link</c>. Every tree format has such values, so the link
/// is shared; the format's view turns the offset into a jump.</summary>
/// <param name="Offset">The value's content offset in the document.</param>
public sealed record ViewInRawLink(long Offset);
