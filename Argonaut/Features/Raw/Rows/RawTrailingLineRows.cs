using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Raw.Rows;

/// <summary>
/// The rows of a document plus the empty line an editor shows after its last newline - or as the
/// only line of an empty document. Neither index has a row for that line: a row is a run of
/// bytes, and the line holds none (<see cref="RawLineRows.Count"/> is 0 for it). But it is where
/// the caret goes after typing Enter at the end, and where an empty document's line 1 is, so the
/// view and the caret ask through this, which adds it as one zero-length row at the end of the
/// data.
///
/// Only once the rows cover every byte: a row added at the end of a partial scan would sit where
/// the next real row belongs.
/// </summary>
public sealed class RawTrailingLineRows : IRawRowIndex
{
    private readonly IRawRowIndex rows;
    private readonly IByteSource source;

    public RawTrailingLineRows(IRawRowIndex rows, IByteSource source)
    {
        this.rows = rows;
        this.source = source;
    }

    /// <summary>The index this adds the line to.</summary>
    public IRawRowIndex Rows => this.rows;

    public int RowCount => HasTrailingLine ? this.rows.RowCount + 1 : this.rows.RowCount;

    public RawRowInfo GetRowInfo(int rowIndex)
    {
        int count = this.rows.RowCount;
        if (rowIndex != count || !HasTrailingLine)
            return this.rows.GetRowInfo(rowIndex);

        long end = this.source.AvailableLength;
        return new RawRowInfo(end, end, false, TrailingLineNumber(count));
    }

    public int? RowForOffset(long offset)
    {
        if (this.rows.RowForOffset(offset) is int row)
            return row;

        return offset == this.source.AvailableLength && HasTrailingLine ? this.rows.RowCount : null;
    }

    public int? LineContaining(int rowIndex)
    {
        int count = this.rows.RowCount;
        return rowIndex == count && HasTrailingLine ? TrailingLineNumber(count) : this.rows.LineContaining(rowIndex);
    }

    private int TrailingLineNumber(int count) => count == 0 ? 1 : (this.rows.LineContaining(count - 1) ?? 0) + 1;

    /// <summary>The data is empty or ends in '\n', and every byte of it is in a row.</summary>
    private bool HasTrailingLine
    {
        get
        {
            if (!this.source.LengthSettled || this.rows is RawSegmentIndex { CoversAllBytes: false })
                return false;

            long length = this.source.AvailableLength;
            return length == 0 || this.source.ByteAt(length - 1) == (byte)'\n';
        }
    }
}
