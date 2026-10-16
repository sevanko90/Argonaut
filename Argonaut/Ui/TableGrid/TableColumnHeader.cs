using System.Collections.Generic;

namespace Argonaut.Ui.TableGrid;

/// <summary>
/// A column's header drawn as pieces, some of which are links - what a grid shows instead of the
/// plain column name when a click on part of the header does something (the JSON array table's
/// route headers, whose container pieces open or collapse columns). The pieces read in order as
/// the header's text; <see cref="Tip"/> is shown over the parts that are not links.
/// </summary>
public sealed record TableColumnHeader(IReadOnlyList<TableHeaderPiece> Pieces, string Tip);

/// <summary>One piece of a <see cref="TableColumnHeader"/>.</summary>
/// <param name="Text">What the piece reads, separator included.</param>
/// <param name="LinkTip">What clicking the piece does, said in its tooltip; null when the piece
/// is plain text and clicking it does nothing.</param>
public readonly record struct TableHeaderPiece(string Text, string? LinkTip)
{
    public bool IsLink => LinkTip is not null;
}

/// <summary>A click on a link piece of a column's header: which column, and which piece of its
/// header.</summary>
public readonly record struct TableHeaderLink(int Column, int Piece);
