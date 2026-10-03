using System;
using System.Collections.Generic;

namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// Colours one row of the raw view from its drawn text. Line-local: the state resets at every
/// line start, so a construct that spans lines is never coloured across them.
/// </summary>
public interface IRawLexer
{
    /// <summary>Shown in the toolbar picker.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Appends the spans of one row to <paramref name="spans"/> (which the caller clears), in
    /// ascending, non-overlapping order, and returns the state at the row's end. Called with
    /// <see cref="RawLexState.LineStart"/> for a row that begins a line, otherwise with the
    /// previous row's return value. Must not allocate. Never called with
    /// <see cref="RawLexState.Unknown"/> - the caller handles that.
    /// </summary>
    RawLexState Lex(ReadOnlySpan<char> row, RawLexState entry, List<RawStyledSpan> spans);
}
