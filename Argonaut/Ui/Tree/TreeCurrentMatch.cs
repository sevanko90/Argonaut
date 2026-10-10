namespace Argonaut.Ui.Tree;

/// <summary>
/// The find match the find bar is on, as the tree can draw it: the row holding it, and which of
/// the term's occurrences in that row's text it is, counting from zero. Find reports byte
/// offsets and rows show display text, so whoever owns the bytes works out the occurrence.
/// </summary>
public readonly record struct TreeCurrentMatch((long ValueStart, bool IsClose) RowKey, int Occurrence);
