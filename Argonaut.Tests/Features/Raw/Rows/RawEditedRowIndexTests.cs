using System.Collections.Generic;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw.Editing;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests.Features.Raw.Rows;

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

        public EditedDocument(byte[] original, int wrapWidth, int maxHeldLines = int.MaxValue, int maxLinesPerRun = RawEditedRowIndex.MaxLinesPerRun)
        {
            this.originalBytes = original;
            this.wrapWidth = wrapWidth;

            OriginalSource = new MemoryByteSource(original);
            Original = RawSegmentIndex.StartIndexing(OriginalSource, wrapWidth);
            Original.IndexingTask.GetAwaiter().GetResult();

            Table = new RawPieceTable(OriginalSource);
            Rows = new RawEditedRowIndex(Original, Table, maxHeldLines, maxLinesPerRun);
            Oracle = new List<byte>(original);
        }

        public MemoryByteSource OriginalSource { get; }
        public RawSegmentIndex Original { get; }
        public RawPieceTable Table { get; }
        public RawEditedRowIndex Rows { get; }
        public List<byte> Oracle { get; }

        /// <summary>
        /// Where a row of the <i>original</i> index starts. Line-boundary tests need this before
        /// any editing, because that is the only coordinate space in which "row 256" means the
        /// same thing before and after an edit.
        /// </summary>
        public long OriginalRowStart(int row) => Original.GetRowInfo(row).Start;

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

        /// <summary>
        /// One replacement, the way <see cref="RawEditController"/> makes one: the piece table
        /// does the delete and the insert, and the row index is told about the pair as a single
        /// extent rather than as two.
        /// </summary>
        public void Replace(long offset, int length, byte[] bytes)
        {
            Rows.ApplyEdit(Table.Replace(offset, length, bytes));
            Oracle.RemoveRange((int)offset, length);
            Oracle.InsertRange((int)offset, bytes);
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

    /// <summary>Many lines, so edits can be put a long way apart.</summary>
    private static EditedDocument LongDocument(int lines, int wrapWidth = 80, int maxHeldLines = int.MaxValue, int maxLinesPerRun = RawEditedRowIndex.MaxLinesPerRun)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines; i++)
            text.Append($"line {i}\n");

        return new EditedDocument(Bytes(text.ToString()), wrapWidth, maxHeldLines, maxLinesPerRun);
    }

    [Fact]
    public void EditsFarApart_BecomeSeparateSpansRatherThanOneCoveringTheGap()
    {
        var document = LongDocument(20_000);

        document.Insert(0, Bytes("z"));
        document.Insert(document.Table.AvailableLength - 1, Bytes("z"));

        // The whole point of several spans: the cost tracks the number of places edited, not the
        // distance between them. One span would have held every line of the 20,000 in between.
        Assert.Equal(2, document.Rows.SpanCount);
        Assert.Equal(2, document.Rows.HeldLines);
        Assert.False(document.Rows.NeedsRebuild);

        document.AssertMatchesAFreshIndex("two edits at opposite ends");
    }

    [Fact]
    public void ReturningToAnEarlierEdit_ReusesItsSpanRatherThanOpeningMore()
    {
        var document = LongDocument(20_000);

        // Ping-ponging between two places is the case that would otherwise accumulate spans
        // without bound; a span is a place, not a keystroke.
        for (int i = 0; i < 10; i++)
        {
            document.Insert(100 + i, Bytes("a"));
            document.Insert(document.Table.AvailableLength - 100, Bytes("b"));
        }

        Assert.Equal(2, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("ten round trips between two places");
    }

    [Fact]
    public void EnoughSeparateEditSites_RaiseNeedsRebuild()
    {
        // Each site costs its own line record, so the budget is what eventually says a full
        // re-index is the better deal - not the distance between the edits. Sites are spaced
        // well apart so each opens a span of its own.
        //
        // Against a budget rather than the production one: half a million line records would
        // take half a million edit sites to reach, and the mechanism under test is the same
        // either way.
        const int SiteSpacing = 1500;
        const int Budget = 40;
        var document = LongDocument(300_000, maxHeldLines: Budget);
        Assert.False(document.Rows.NeedsRebuild);

        int sites = 0;
        while (document.Rows.HeldLines <= Budget)
        {
            long at = SiteSpacing + (sites * (long)SiteSpacing);
            Assert.True(at < document.Table.AvailableLength, "ran out of document before the budget");
            document.Insert(at, Bytes("z"));
            sites++;
        }

        Assert.Equal(sites, document.Rows.SpanCount);
        Assert.Equal(sites, document.Rows.HeldLines);
        Assert.True(document.Rows.NeedsRebuild);

        // Over budget, a new place is refused and an existing one is not.
        Assert.False(document.Rows.CanAbsorbEditAt(document.Table.AvailableLength - 10));
        Assert.True(document.Rows.CanAbsorbEditAt(SiteSpacing));

        // Correctness does not depend on the rebuild happening - it is an efficiency signal.
        document.AssertMatchesAFreshIndex("budget exhausted");
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
                new RawEditedRowIndex(unfinished, new RawPieceTable(source)));
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
    // ---- line boundaries ------------------------------------------------------------------
    //
    // A span is a run of whole lines, so a line boundary is where every decision this class makes
    // changes its answer: which line an edit's first and last byte belong to, whether an edit
    // joins the span before or after it, and where the rebuilt run's original coordinates come
    // from. An off-by-one in any of those is invisible everywhere else in the file and certain
    // here, so the boundary cases are tested directly rather than left to the random scripts.

    /// <summary>Where line <paramref name="line"/> (0-based) of a <see cref="LongDocument"/> starts.
    /// Every line there is one row at the widths used, which the harness asserts rather than
    /// assumes.</summary>
    private static long LineStart(EditedDocument document, int line)
    {
        var info = document.Original.GetRowInfo(line);
        Assert.Equal(line + 1, info.LineNumber);
        return info.Start;
    }

    [Theory]
    [InlineData(-1)]  // the newline ending the line before
    [InlineData(0)]   // exactly on the line start
    [InlineData(1)]   // just inside it
    public void InsertAtALineBoundary_MatchesAFreshIndex(int nudge)
    {
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = LineStart(document, 256) + nudge;

        document.Insert(at, Bytes("INSERTED"));

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex($"insert at line 256 {nudge:+0;-0;+0}");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void InsertingANewlineAtALineBoundary_MatchesAFreshIndex(int nudge)
    {
        // A newline is the insertion that changes the line count as well as the offsets, so the
        // row arithmetic and the line arithmetic both have to hold across the boundary.
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = LineStart(document, 256) + nudge;

        document.Insert(at, Bytes("\n"));

        document.AssertMatchesAFreshIndex($"newline at line 256 {nudge:+0;-0;+0}");
    }

    [Theory]
    [InlineData(0, 6)]    // starts exactly on the boundary
    [InlineData(-6, 6)]   // ends exactly on it
    [InlineData(-3, 6)]   // straddles it
    [InlineData(-3, 200)] // straddles it and takes several lines with it
    public void DeleteAcrossALineBoundary_MatchesAFreshIndex(int startNudge, int length)
    {
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = LineStart(document, 256) + startNudge;

        document.Delete(at, length);

        document.AssertMatchesAFreshIndex($"delete {length} from line 256 {startNudge:+0;-0;+0}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ReplaceAcrossALineBoundary_MatchesAFreshIndex(int startNudge)
    {
        // Same length in and out, so nothing moves - but the bytes a forced break backs off from
        // do change, which is the one way a zero-delta edit can still re-flow rows.
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = LineStart(document, 256) + startNudge;

        document.Replace(at, 6, Bytes("éé\nZ"));

        document.AssertMatchesAFreshIndex($"replace at line 256 {startNudge:+0;-0;+0}");
    }

    [Fact]
    public void EditsInTheSameLine_ShareOneSpanOfOneLine()
    {
        var document = LongDocument(2_000, wrapWidth: 40);
        long line = LineStart(document, 256);

        document.Insert(line + 1, Bytes("a"));
        document.Insert(line + 4, Bytes("b"));
        document.Insert(line, Bytes("c"));

        var span = Assert.Single(EnumerateSpans(document));
        Assert.Equal(1, span.LinesHeld);
        document.AssertMatchesAFreshIndex("three edits in one line");
    }

    [Fact]
    public void EditsOnAdjacentLines_MergeIntoOneSpan()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(LineStart(document, 256) + 1, Bytes("a"));
        document.Insert(LineStart(document, 257) + 2, Bytes("b"));   // the line after
        document.Insert(LineStart(document, 255) + 1, Bytes("c"));   // the line before

        var span = Assert.Single(EnumerateSpans(document));
        Assert.Equal(3, span.LinesHeld);
        document.AssertMatchesAFreshIndex("edits on three adjacent lines");
    }

    [Fact]
    public void OneUntouchedLineBetweenEdits_GivesTwoSpans()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(LineStart(document, 256) + 1, Bytes("a"));
        document.Insert(LineStart(document, 258) + 2, Bytes("b"));

        Assert.Equal(2, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("edits either side of one untouched line");

        // Editing the line between touches neither span's lines, but abuts both: it joins them.
        document.Insert(LineStart(document, 257) + 1, Bytes("c"));

        var span = Assert.Single(EnumerateSpans(document));
        Assert.Equal(3, span.LinesHeld);
        document.AssertMatchesAFreshIndex("the line between, edited too");
    }

    [Fact]
    public void EditingBackwardsAcrossLines_KeepsTheSpansInOrder()
    {
        // Opening spans out of order is the case where an insert into the middle of the list has
        // to renumber every span after it; doing it in reverse guarantees that path runs.
        var document = LongDocument(2_000, wrapWidth: 40);

        foreach (int line in new[] { 1280, 896, 512, 128 })
        {
            document.Insert(LineStart(document, line), Bytes("z"));
            document.AssertMatchesAFreshIndex($"inserted at line {line}");
        }

        Assert.Equal(4, document.Rows.SpanCount);
    }

    [Fact]
    public void ADeleteSpanningSpans_LeavesOne()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        long first = LineStart(document, 256);
        long second = LineStart(document, 768);
        document.Insert(first, Bytes("a"));
        document.Insert(second, Bytes("b"));
        Assert.Equal(2, document.Rows.SpanCount);

        // A delete running from the first edit past the second: the second span's lines no longer
        // exist, and its byte delta has to survive inside the first span or the document's total
        // delta stops adding up.
        document.Delete(first, (int)(second + 1 - first) + 20);

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("delete swallowing the span after it");
    }

    [Fact]
    public void ANewlineAtALineStartAndAtTheEnd_MatchAFreshIndex()
    {
        var document = Document("alpha\nbeta\ngamma\n");

        document.Insert(6, Bytes("\n"));
        document.AssertMatchesAFreshIndex("newline at the start of line 2");

        document.Insert(document.Table.AvailableLength, Bytes("\n"));
        document.AssertMatchesAFreshIndex("newline after the trailing newline");

        document.Insert(document.Table.AvailableLength, Bytes("tail"));
        document.AssertMatchesAFreshIndex("an unterminated line after that");

        document.Delete(document.Table.AvailableLength - 6, 6);
        document.AssertMatchesAFreshIndex("both trailing lines deleted again");
    }

    [Fact]
    public void ARunReachingMaxLinesPerRun_SplitsAndStillMatches()
    {
        // At a cap of 8 lines per run, a 30-line paste has to become several adjacent runs - and
        // then an edit inside one of them rebuilds only that one.
        const int Cap = 8;
        var document = LongDocument(200, wrapWidth: 40, maxLinesPerRun: Cap);

        var paste = new StringBuilder();
        for (int i = 0; i < 30; i++)
            paste.Append($"pasted {i}\n");

        document.Insert(LineStart(document, 100), Bytes(paste.ToString()));
        document.AssertMatchesAFreshIndex("30-line paste at a cap of 8");

        var spans = EnumerateSpans(document).ToList();
        Assert.True(spans.Count >= 4, $"{spans.Count} spans for 31 lines at a cap of {Cap}");
        Assert.All(spans, span => Assert.True(span.LinesHeld <= Cap, $"span {span.Index} holds {span.LinesHeld} lines"));

        document.Insert(LineStart(document, 100) + 100, Bytes("typed"));
        Assert.True(document.Rows.LinesRebuiltInLastEdit <= Cap,
            $"typing inside the paste rebuilt {document.Rows.LinesRebuiltInLastEdit} lines");
        document.AssertMatchesAFreshIndex("typing inside the paste");

        document.Delete(LineStart(document, 100) + 50, 120);
        document.AssertMatchesAFreshIndex("deleting across runs of the paste");
    }

    // ---- long lines -----------------------------------------------------------------------

    /// <summary>
    /// A long line, plus ordinary lines after it, so an edit can be placed inside the line, at
    /// its far end, and past it.
    /// </summary>
    private static (EditedDocument Document, long LineEnd) LongLineDocument(int lineBytes)
    {
        var text = new StringBuilder();
        text.Append(new string('a', lineBytes)).Append('\n');
        for (int i = 0; i < 400; i++)
            text.Append($"line {i} after the long one\n");

        return (new EditedDocument(Bytes(text.ToString()), wrapWidth: 80), lineBytes + 1);
    }

    [Fact]
    public void AnEditInsideAVeryLongLine_HoldsOneLineRecordAndScansOnlyTheInsert()
    {
        // The case that came out of running the editor on a 4GB document: a 48-byte edit inside a
        // ~54MB unbroken line once produced a span of 676,661 rows and 21MB, then - holding anchors
        // instead - a walk of every row to the line's newline on every keystroke. Neither is
        // needed: the line's rows are arithmetic from its start.
        var (document, lineEnd) = LongLineDocument(400_000);

        document.Insert(100, Bytes("EDIT"));

        var span = Assert.Single(EnumerateSpans(document));
        Assert.Equal(1, span.LinesHeld);
        Assert.Equal(4, document.Rows.BytesScannedInLastEdit);
        Assert.Equal(1, document.Rows.LinesRebuiltInLastEdit);

        // A second edit near the line's far end: the same span, the same one record.
        document.Insert(lineEnd - 500, Bytes("y"));

        Assert.Equal(1, document.Rows.SpanCount);
        Assert.Equal(1, document.Rows.HeldLines);
        Assert.Equal(1, document.Rows.BytesScannedInLastEdit);
        Assert.Equal(1, document.Rows.LinesRebuiltInLastEdit);

        document.AssertMatchesAFreshIndex("edits at both ends of a 400KB line");
    }

    [Fact]
    public void RowsDeepInsideALongLinesSpan_ComeOutOfTheLineGeometry()
    {
        // Every row of a long line's span is computed rather than stored, so this reads every row
        // rather than only its edges - over multi-byte content, where the backoffs are real.
        var text = new StringBuilder();
        while (text.Length < 60_000)
            text.Append("bébé日x");
        text.Append('\n').Append("after\n");

        var document = new EditedDocument(Bytes(text.ToString()), 80);
        document.Insert(40, Bytes("Z"));

        document.AssertMatchesAFreshIndex("every row of a long line's span");
    }

    [Fact]
    public void AnEditPastALongLinesSpan_IsCheapToo()
    {
        var (document, lineEnd) = LongLineDocument(400_000);

        document.Insert(100, Bytes("x"));
        document.Insert(lineEnd + 2_000, Bytes("y"));

        Assert.Equal(2, document.Rows.SpanCount);
        Assert.Equal(1, document.Rows.LinesRebuiltInLastEdit);

        document.AssertMatchesAFreshIndex("edit past a long line's span");
    }

    [Fact]
    public void AnEditInA32MBLine_ScansOnlyWhatItInserted()
    {
        // Too big for the per-offset oracle, so spot checks: the row count, the rows around the
        // edit and at the line's end, and the first row after it.
        const int LineBytes = 32 * 1024 * 1024;
        var content = new byte[LineBytes + 6];
        Array.Fill(content, (byte)'q', 0, LineBytes);
        "\nlast\n"u8.CopyTo(content.AsSpan(LineBytes));

        var document = new EditedDocument(content, 80);
        document.Insert(1_000, Bytes("inserted"));

        Assert.Equal(8, document.Rows.BytesScannedInLastEdit);
        Assert.Equal(1, document.Rows.HeldLines);

        var fresh = RawSegmentIndex.StartIndexing(new MemoryByteSource(document.Oracle.ToArray()), 80);
        fresh.IndexingTask.GetAwaiter().GetResult();
        Assert.Equal(fresh.RowCount, document.Rows.RowCount);
        foreach (int row in new[] { 0, 11, 12, 13, 5000, fresh.RowCount - 3, fresh.RowCount - 2, fresh.RowCount - 1 })
        {
            Assert.Equal(fresh.GetRowInfo(row), document.Rows.GetRowInfo(row));
            Assert.Equal(fresh.LineContaining(row), document.Rows.LineContaining(row));
        }

        foreach (long offset in new long[] { 0, 999, 1_000, 1_007, 1_008, 1_000_000, LineBytes + 7, LineBytes + 8 })
            Assert.Equal(fresh.RowForOffset(offset), document.Rows.RowForOffset(offset));
    }

    [Fact]
    public void TypingInsideAnExistingSpan_AllocatesNothing()
    {
        // The ordinary keystroke - one edit inside one span - reuses that span's records and the
        // rebuild scratch list, so once they have grown it allocates nothing. Only the row index
        // is measured: the piece table's own bookkeeping is not this class's to account for.
        var (document, _) = LongLineDocument(400_000);
        for (int i = 0; i < 16; i++)
            document.Insert(1_000 + i, Bytes("w"));

        var extents = new RawEditExtent[64];
        long allocated = 0;
        for (int i = 0; i < extents.Length; i++)
        {
            var extent = document.Table.Insert(2_000 + i, "z"u8);
            long before = GC.GetAllocatedBytesForCurrentThread();
            document.Rows.ApplyEdit(extent);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            document.Oracle.Insert(2_000 + i, (byte)'z');
        }

        Assert.Equal(0, allocated);
        Assert.Equal(1, document.Rows.HeldLines);
    }

    private static IEnumerable<RawSpanSnapshot> EnumerateSpans(EditedDocument document)
        => document.Rows.DescribeSpans();

    /// <summary>
    /// The same oracle over a document big enough that random edits land in many different
    /// spans at once - which is where the multi-span bookkeeping can go wrong without any single
    /// span being wrong. Every lookup after an edit has to cross spans, gaps and the displaced
    /// tail, and each of the three reads its answer from a different place.
    /// </summary>
    [Theory]
    [InlineData(11, 40)]
    [InlineData(12, 80)]
    public void ScatteredRandomEdits_AlwaysMatchAFreshIndex(int seed, int wrapWidth)
    {
        var random = new Random(seed);

        var text = new StringBuilder();
        for (int i = 0; text.Length < 30_000; i++)
            text.Append($"line {i} {new string('x', random.Next(0, 90))}\n");

        var document = new EditedDocument(Bytes(text.ToString()), wrapWidth);

        for (int step = 0; step < 30; step++)
        {
            long length = document.Table.AvailableLength;
            if (random.Next(100) < 35)
            {
                int offset = random.Next((int)length);
                document.Delete(offset, random.Next(1, Math.Min(40, (int)length - offset + 1)));
            }
            else
            {
                document.Insert(random.Next((int)length + 1), Bytes(random.Next(3) switch
                {
                    0 => "\n",
                    1 => "é",
                    _ => new string('q', random.Next(1, 30))
                }));
            }

            document.AssertMatchesAFreshIndex($"scattered seed {seed}, step {step}");
        }

        // The point of the exercise: this really did spread across many spans.
        Assert.True(document.Rows.SpanCount > 3, $"only {document.Rows.SpanCount} spans");
    }

    /// <summary>
    /// The random script again at a cap of three lines per run, so nearly every edit splits a
    /// region into several runs, joins abutting ones, or deletes across them - the bookkeeping
    /// the default cap only reaches inside a big paste.
    /// </summary>
    [Theory]
    [InlineData(21, 8)]
    [InlineData(22, 16)]
    [InlineData(23, 40)]
    public void RandomEditScriptAtATinyRunCap_AlwaysMatchesAFreshIndex(int seed, int wrapWidth)
    {
        var random = new Random(seed);

        var text = new StringBuilder();
        while (text.Length < 1500)
        {
            text.Append(random.Next(3) == 0 ? new string('日', random.Next(0, 30)) : new string('x', random.Next(0, 70)));
            text.Append(random.Next(8) == 0 ? "\r\n" : "\n");
        }

        var document = new EditedDocument(Bytes(text.ToString()), wrapWidth, maxLinesPerRun: 3);

        for (int step = 0; step < 60; step++)
        {
            long length = document.Table.AvailableLength;
            int choice = random.Next(100);
            if (length > 0 && choice < 35)
            {
                int offset = random.Next((int)length);
                document.Delete(offset, random.Next(1, Math.Min(80, (int)length - offset + 1)));
            }
            else if (length > 0 && choice < 45)
            {
                int offset = random.Next((int)length);
                document.Replace(offset, random.Next(0, Math.Min(10, (int)length - offset + 1)), Bytes("😀\n"));
            }
            else
            {
                document.Insert(random.Next((int)length + 1), Bytes(random.Next(4) switch
                {
                    0 => "\n",
                    1 => "a\nb\nc\nd\ne\n",
                    2 => new string('é', random.Next(1, 60)),
                    _ => new string('q', random.Next(1, 90))
                }));
            }

            document.AssertMatchesAFreshIndex($"tiny cap seed {seed}, step {step}");
        }
    }

    /// <summary>
    /// The same property under conditions chosen to stress line boundaries rather than reflow: a
    /// tiny wrap cap and newline-dense content, so rows are short, forced breaks and real line
    /// ends alternate constantly, and edits routinely land on a newline, beside one, or at the
    /// start of a line - exactly where the line an edit belongs to, and whether it joins the span
    /// before or after it, changes its answer.
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
    /// A newline inserted exactly onto a soft-wrap boundary: the original wraps 16 x's at a cap of
    /// 8, so the row beginning at byte 8 is a continuation, and after the insert the row starting
    /// there begins line 2 for real. The line records have to say so - the row that genuinely
    /// starts line 2 must not report the blank gutter the original continuation row had.
    /// </summary>
    [Fact]
    public void ANewlineOnASoftWrapBoundary_StartsARealLine()
    {
        var document = Document(new string('x', 16), wrapWidth: 8);

        document.Insert(8, Bytes("\n"));

        Assert.Equal(2, document.Rows.GetRowInfo(1).LineNumber);
        document.AssertMatchesAFreshIndex("newline inserted onto a soft-wrap boundary");
    }
}
