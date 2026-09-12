using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The oracle here is the strongest one available: indexing the edited bytes from scratch. If
/// <see cref="RawEditedRowIndex"/> ever reports a row, a row count or an offset lookup that
/// differs from what a fresh <see cref="RawSegmentIndex"/> over the same bytes reports, it is
/// wrong - there is no judgement call about what the right answer is.
///
/// Every assertion runs after every single edit, so a divergence is attributed to the edit that
/// caused it rather than showing up as a mismatch at the end of a long script.
/// </summary>
public class RawEditedRowIndexTests
{
    private sealed class EditedDocument
    {
        private readonly byte[] originalBytes;
        private readonly int wrapWidth;

        public EditedDocument(byte[] original, int wrapWidth)
        {
            this.originalBytes = original;
            this.wrapWidth = wrapWidth;

            OriginalSource = new MemoryByteSource(original);
            var index = RawSegmentIndex.StartIndexing(OriginalSource, wrapWidth);
            index.IndexingTask.GetAwaiter().GetResult();

            Table = new RawPieceTable(OriginalSource);
            Rows = new RawEditedRowIndex(index, OriginalSource, Table);
            Oracle = new List<byte>(original);
        }

        public MemoryByteSource OriginalSource { get; }
        public RawPieceTable Table { get; }
        public RawEditedRowIndex Rows { get; }
        public List<byte> Oracle { get; }

        public void Insert(long offset, byte[] bytes)
        {
            Rows.ApplyEdit(Table.Insert(offset, bytes));
            Oracle.InsertRange((int)offset, bytes);
        }

        public void Delete(long offset, int length)
        {
            Rows.ApplyEdit(Table.Delete(offset, length));
            Oracle.RemoveRange((int)offset, length);
        }

        /// <summary>Indexes the edited bytes from scratch and demands identical answers.</summary>
        public void AssertMatchesAFreshIndex(string because)
        {
            byte[] edited = Oracle.ToArray();
            Assert.Equal(edited.Length, Table.AvailableLength);

            var freshSource = new MemoryByteSource(edited);
            var fresh = RawSegmentIndex.StartIndexing(freshSource, this.wrapWidth);
            fresh.IndexingTask.GetAwaiter().GetResult();

            Assert.True(fresh.RowCount == Rows.RowCount,
                $"{because}: row count {Rows.RowCount}, fresh index says {fresh.RowCount}");

            for (int row = 0; row < fresh.RowCount; row++)
            {
                var expected = fresh.GetRowInfo(row);
                var actual = Rows.GetRowInfo(row);
                Assert.True(expected == actual, $"{because}: row {row} is {actual}, fresh index says {expected}");

                // The line a row sits in, which a continuation row does not report through
                // RawRowInfo. The caret readout asks for it, so an edit must not disturb it.
                Assert.True(fresh.LineContaining(row) == Rows.LineContaining(row),
                    $"{because}: row {row} is in line {Rows.LineContaining(row)}, "
                    + $"fresh index says {fresh.LineContaining(row)}");
            }

            for (long offset = 0; offset < edited.Length; offset++)
                Assert.True(fresh.RowForOffset(offset) == Rows.RowForOffset(offset),
                    $"{because}: offset {offset} maps to row {Rows.RowForOffset(offset)}, fresh index says {fresh.RowForOffset(offset)}");

            Assert.Null(Rows.RowForOffset(edited.Length));
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static EditedDocument Document(string text, int wrapWidth = 16)
        => new(Bytes(text), wrapWidth);

    [Fact]
    public void WithNoEdits_ReadsExactlyLikeTheOriginalIndex()
    {
        var document = Document("alpha\nbeta\ngamma\n");

        document.AssertMatchesAFreshIndex("unedited");
    }

    [Fact]
    public void InsertWithinALine_KeepsEveryOtherRowWhereItWas()
    {
        var document = Document("alpha\nbeta\ngamma\n");

        document.Insert(2, Bytes("XX"));

        document.AssertMatchesAFreshIndex("insert inside line 1");
    }

    [Fact]
    public void InsertingANewline_SplitsTheLineAndRenumbersTheRest()
    {
        var document = Document("alpha\nbeta\ngamma\n");

        document.Insert(2, Bytes("\n"));

        document.AssertMatchesAFreshIndex("newline inserted into line 1");
    }

    [Fact]
    public void DeletingANewline_JoinsTwoLines()
    {
        var document = Document("alpha\nbeta\ngamma\n");

        document.Delete(5, 1);

        document.AssertMatchesAFreshIndex("newline removed");
    }

    [Fact]
    public void EditAtTheVeryStart_IsHandled()
    {
        var document = Document("alpha\nbeta\n");

        document.Insert(0, Bytes("zzz\n"));

        document.AssertMatchesAFreshIndex("insert at offset 0");
    }

    [Fact]
    public void EditAtTheVeryEnd_IsHandled()
    {
        var document = Document("alpha\nbeta\n");

        document.Insert(document.Table.AvailableLength, Bytes("omega"));

        document.AssertMatchesAFreshIndex("append at end of document");
    }

    [Fact]
    public void TypingIntoAnEmptyDocument_Works()
    {
        var document = Document(string.Empty);

        document.Insert(0, Bytes("a"));
        document.AssertMatchesAFreshIndex("first character");

        document.Insert(1, Bytes("b\nc"));
        document.AssertMatchesAFreshIndex("more characters");
    }

    [Fact]
    public void DeletingTheWholeDocument_LeavesNoRows()
    {
        var document = Document("alpha\nbeta\n");

        document.Delete(0, (int)document.Table.AvailableLength);

        Assert.Equal(0, document.Rows.RowCount);
        Assert.Null(document.Rows.RowForOffset(0));
    }

    [Fact]
    public void InsertShiftingAForcedBreak_ReflowsTheWholeLine()
    {
        // One long line at a narrow cap, so it occupies many soft-wrapped rows and an insert
        // near its start displaces every forced break after it.
        var document = Document(new string('x', 200), wrapWidth: 16);

        document.Insert(3, Bytes("YYYY"));

        document.AssertMatchesAFreshIndex("insert reflowing a wrapped line");
    }

    [Fact]
    public void InsertChangingTheUtf8Backoff_IsStillExact()
    {
        // Forced breaks back off up to 3 bytes to avoid splitting a character, so shifting
        // multibyte content across the cap moves the break by an amount no arithmetic predicts.
        var document = Document(string.Concat(new string('é', 60)), wrapWidth: 16);

        document.Insert(1, Bytes("z"));
        document.AssertMatchesAFreshIndex("one byte shifting every backoff");

        document.Insert(0, Bytes("zz"));
        document.AssertMatchesAFreshIndex("two more bytes");
    }

    [Fact]
    public void SeveralEditsInTheSameArea_CoalesceIntoOneDirtySpan()
    {
        var document = Document("alpha\nbeta\ngamma\ndelta\n");

        document.Insert(2, Bytes("1"));
        document.AssertMatchesAFreshIndex("edit 1");

        document.Insert(8, Bytes("2"));
        document.AssertMatchesAFreshIndex("edit 2");

        document.Delete(3, 2);
        document.AssertMatchesAFreshIndex("edit 3");

        document.Insert(0, Bytes("3"));
        document.AssertMatchesAFreshIndex("edit 4, before the others");
    }

    [Fact]
    public void EditsSpanningManyAnchorBuckets_RaiseNeedsRebuild()
    {
        // More rows than the re-derivation is willing to hold; editing the first line and the
        // last forces the dirty span to cover everything between them.
        var text = new StringBuilder();
        for (int i = 0; i < RawEditedRowIndex.MaxDerivedRows + 500; i++)
            text.Append($"line {i}\n");

        var document = new EditedDocument(Bytes(text.ToString()), wrapWidth: 80);
        Assert.False(document.Rows.NeedsRebuild);

        document.Insert(document.Table.AvailableLength - 1, Bytes("z"));
        Assert.False(document.Rows.NeedsRebuild);

        document.Insert(0, Bytes("z"));

        Assert.True(document.Rows.NeedsRebuild);
        Assert.True(document.Rows.DerivedRowCount > RawEditedRowIndex.MaxDerivedRows);

        // Correctness does not depend on the rebuild happening - it is an efficiency signal.
        document.AssertMatchesAFreshIndex("dirty span covering the file");
    }

    [Fact]
    public void ScanMustBeCompleteBeforeEditsAreLayeredOn()
    {
        var source = new MemoryByteSource(Bytes("alpha\n"));
        var index = RawSegmentIndex.StartIndexing(source, 80);
        index.IndexingTask.GetAwaiter().GetResult();

        // The guard itself: an index that has not finished is refused.
        var unfinished = RawSegmentIndex.StartIndexing(new MemoryByteSource(new byte[8 * 1024 * 1024]), 80);
        if (!unfinished.AllItemsPublished)
        {
            Assert.Throws<ArgumentException>(() =>
                new RawEditedRowIndex(unfinished, source, new RawPieceTable(source)));
        }

        unfinished.IndexingTask.GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(7, 16)]
    [InlineData(99, 16)]
    [InlineData(2024, 80)]
    public void RandomEditScript_AlwaysMatchesAFreshIndex(int seed, int wrapWidth)
    {
        var random = new Random(seed);

        // Mixed content: short and long lines, CRLF, multibyte - every break rule in play.
        var text = new StringBuilder();
        while (text.Length < 3000)
        {
            int lineLength = random.Next(0, 60);
            text.Append(random.Next(4) == 0 ? new string('é', lineLength / 2) : new string('x', lineLength));
            text.Append(random.Next(8) == 0 ? "\r\n" : "\n");
        }

        var document = new EditedDocument(Bytes(text.ToString()), wrapWidth);

        for (int step = 0; step < 40; step++)
        {
            long length = document.Table.AvailableLength;
            if (length > 0 && random.Next(100) < 40)
            {
                int offset = random.Next((int)length);
                int count = random.Next(1, Math.Min(30, (int)length - offset + 1));
                document.Delete(offset, count);
            }
            else
            {
                int offset = random.Next((int)length + 1);
                string payload = random.Next(4) switch
                {
                    0 => "\n",
                    1 => "é",
                    2 => new string('q', random.Next(1, 25)),
                    _ => "\r\n"
                };
                document.Insert(offset, Bytes(payload));
            }

            document.AssertMatchesAFreshIndex($"seed {seed}, step {step}");
        }
    }
    /// <summary>
    /// The same property under conditions chosen to stress re-convergence rather than reflow: a
    /// tiny wrap cap and newline-dense content, so rows are short, forced breaks and real line
    /// ends alternate constantly, and edits routinely land on the byte immediately before a
    /// candidate convergence point. That byte is the one place the two streams can reach the same
    /// offset while disagreeing about whether a line starts there - it is still inside the edit,
    /// so it is not required to match.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void NewlineDenseRandomEdits_AlwaysMatchAFreshIndex(int seed)
    {
        var random = new Random(seed);

        var text = new StringBuilder();
        while (text.Length < 400)
        {
            text.Append(new string('x', random.Next(0, 12)));
            text.Append(random.Next(6) == 0 ? "\r\n" : "\n");
        }

        var document = new EditedDocument(Bytes(text.ToString()), wrapWidth: 8);

        for (int step = 0; step < 60; step++)
        {
            long length = document.Table.AvailableLength;
            if (length > 0 && random.Next(100) < 45)
            {
                int offset = random.Next((int)length);
                int count = random.Next(1, Math.Min(6, (int)length - offset + 1));
                document.Delete(offset, count);
            }
            else
            {
                int offset = random.Next((int)length + 1);
                string payload = random.Next(5) switch
                {
                    0 => "\n",
                    1 => "\r\n",
                    2 => "x",
                    3 => new string('y', random.Next(1, 10)),
                    _ => "é"
                };
                document.Insert(offset, Bytes(payload));
            }

            document.AssertMatchesAFreshIndex($"seed {seed}, step {step}");
        }
    }
    /// <summary>
    /// Re-convergence needs the two streams to agree that a line starts there, not merely to sit
    /// at the same offset - and the offset alone is not enough precisely because the byte before
    /// a convergence point can be the last byte an edit inserted, which is not required to match.
    ///
    /// Constructed to be exactly that case: the original wraps 16 x's at a cap of 8, so the row
    /// beginning at byte 8 is a continuation. Inserting a newline at byte 8 makes the edited row
    /// end there for real - same offset once the one-byte delta is applied, opposite line-start
    /// state. Accepting that as convergence hands the rest of the document line numbers taken
    /// from continuation rows, so the row that genuinely starts line 2 reports a blank gutter.
    /// </summary>
    [Fact]
    public void ConvergenceRequiresAgreementOnWhereLinesStart()
    {
        var document = Document(new string('x', 16), wrapWidth: 8);

        document.Insert(8, Bytes("\n"));

        Assert.Equal(2, document.Rows.GetRowInfo(1).LineNumber);
        document.AssertMatchesAFreshIndex("newline inserted onto a soft-wrap boundary");
    }
}
