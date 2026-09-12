namespace Argonaut.Features.Search;

/// <summary>
/// A growing, randomly-addressable run of matches - what <see cref="FindCursor"/> folds from.
/// Implemented by <see cref="SearchSession"/>; exists so the cursor can be driven from a
/// plain list in tests, with no file and no background scan.
///
/// <see cref="MatchCount"/> grows while a scan runs: read it once, then index below it.
/// </summary>
public interface IMatchSource
{
    int MatchCount { get; }

    SearchMatch GetMatch(int index);
}
