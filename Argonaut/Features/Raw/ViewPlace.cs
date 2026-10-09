namespace Argonaut.Features.Raw;

/// <summary>
/// Where the user is in the document, in terms that survive the rows being replaced: a byte
/// offset, which means the same at any wrap width, and the screen row it sits on, counted from
/// the top of the view. A re-wrap changes every row's index and extent, so the old scroll offset
/// is meaningless against the new rows, but this can be turned back into one.
/// </summary>
/// <param name="Offset">The byte offset to keep on screen.</param>
/// <param name="ScreenRow">How many rows below the top of the view the row holding
/// <paramref name="Offset"/> should land; 0 puts it at the top.</param>
internal readonly record struct ViewPlace(long Offset, int ScreenRow);
