using System.Collections.Generic;
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

        public EditedDocument(byte[] original, int wrapWidth, int maxHeldAnchors = int.MaxValue)
        {
            this.originalBytes = original;
            this.wrapWidth = wrapWidth;

            OriginalSource = new MemoryByteSource(original);
            Original = RawSegmentIndex.StartIndexing(OriginalSource, wrapWidth);
            Original.IndexingTask.GetAwaiter().GetResult();

            Table = new RawPieceTable(OriginalSource);
            Rows = new RawEditedRowIndex(Original, OriginalSource, Table, maxHeldAnchors);
            Oracle = new List<byte>(original);
        }

        public MemoryByteSource OriginalSource { get; }
        public RawSegmentIndex Original { get; }
        public RawPieceTable Table { get; }
        public RawEditedRowIndex Rows { get; }
        public List<byte> Oracle { get; }

        /// <summary>
        /// Where a row of the <i>original</i> index starts. Anchor-boundary tests need this
        /// before any editing, because that is the only coordinate space in which "row 64" means
        /// the same thing before and after an edit.
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
    private const int RawSegmentIndexAnchorStride = 64;

    private static EditedDocument LongDocument(int lines, int wrapWidth = 80, int maxHeldAnchors = int.MaxValue)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines; i++)
            text.Append($"line {i}\n");

        return new EditedDocument(Bytes(text.ToString()), wrapWidth, maxHeldAnchors);
    }

    [Fact]
    public void EditsFarApart_BecomeSeparateSpansRatherThanOneCoveringTheGap()
    {
        var document = LongDocument(20_000);

        document.Insert(0, Bytes("z"));
        document.Insert(document.Table.AvailableLength - 1, Bytes("z"));

        // The whole point of several spans: the cost tracks the number of places edited, not the
        // distance between them. One span would have held every row of the 20,000 in between.
        Assert.Equal(2, document.Rows.SpanCount);
        Assert.True(document.Rows.TotalDerivedRows < 4 * RawSegmentIndexAnchorStride,
            $"held {document.Rows.TotalDerivedRows} rows for two edits");
        Assert.False(document.Rows.NeedsRebuild);

        document.AssertMatchesAFreshIndex("two edits at opposite ends");
    }

    [Fact]
    public void EditsCloseTogether_ShareOneSpan()
    {
        var document = LongDocument(20_000);

        document.Insert(1000, Bytes("z"));
        document.Insert(1010, Bytes("z"));
        document.Insert(990, Bytes("z"));

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("three edits within a few bytes");
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
        // Each site costs its own anchor bucket, so the budget is what eventually says a full
        // re-index is the better deal - not the distance between the edits. Sites are spaced
        // well past one anchor stride so each opens a span of its own.
        //
        // Against a budget rather than the production one: a megabyte of anchors would take a
        // third of a gigabyte of document to reach, and the mechanism under test is the same
        // either way.
        const int SiteSpacing = 1500;
        const int Budget = 40;
        var document = LongDocument(300_000, maxHeldAnchors: Budget);
        Assert.False(document.Rows.NeedsRebuild);

        int sites = 0;
        while (document.Rows.HeldAnchors <= Budget)
        {
            long at = SiteSpacing + (sites * (long)SiteSpacing);
            Assert.True(at < document.Table.AvailableLength, "ran out of document before the budget");
            document.Insert(at, Bytes("z"));
            sites++;
        }

        // A site normally opens a span of its own. A few land close enough to the previous
        // span's convergence point to be absorbed into it instead, which is the cheaper of the
        // two and is exactly what the anchor-stride rule is for - so this is "about one span per
        // site", not "exactly one".
        Assert.True(document.Rows.SpanCount <= sites);
        Assert.True(document.Rows.SpanCount > sites * 9 / 10,
            $"{sites} edit sites produced only {document.Rows.SpanCount} spans");

        Assert.True(document.Rows.NeedsRebuild);

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
    // ---- anchor boundaries ----------------------------------------------------------------
    //
    // A span can only begin at an anchor - every 64th row of the ORIGINAL index - so an anchor
    // boundary is where every decision this class makes changes its answer: which anchor a new
    // span starts at, whether an edit is close enough to widen the span before it, and whether a
    // re-derivation ran past the span after it. An off-by-one in any of those is invisible
    // everywhere else in the file and certain here, so the boundary cases are tested directly
    // rather than left to the random scripts to stumble on.

    /// <summary>Row <paramref name="row"/> of the original index must be an anchor row for these
    /// to be testing what they claim; the harness asserts it rather than assuming it.</summary>
    private static long AnchorRowStart(EditedDocument document, int anchorIndex)
    {
        int row = anchorIndex * RawSegmentIndexAnchorStride;
        Assert.True(row < document.Original.RowCount, $"document has no anchor {anchorIndex}");
        return document.OriginalRowStart(row);
    }

    [Theory]
    [InlineData(-1)]  // the byte before an anchor row starts
    [InlineData(0)]   // exactly on it
    [InlineData(1)]   // just inside it
    public void InsertAtAnAnchorBoundary_MatchesAFreshIndex(int nudge)
    {
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = AnchorRowStart(document, 4) + nudge;

        document.Insert(at, Bytes("INSERTED"));

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex($"insert at anchor 4 {nudge:+0;-0;+0}");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void InsertingANewlineAtAnAnchorBoundary_MatchesAFreshIndex(int nudge)
    {
        // A newline is the insertion that changes the row count as well as the offsets, so the
        // anchor arithmetic and the line arithmetic both have to hold across the boundary.
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = AnchorRowStart(document, 4) + nudge;

        document.Insert(at, Bytes("\n"));

        document.AssertMatchesAFreshIndex($"newline at anchor 4 {nudge:+0;-0;+0}");
    }

    [Theory]
    [InlineData(0, 6)]    // starts exactly on the boundary
    [InlineData(-6, 6)]   // ends exactly on it
    [InlineData(-3, 6)]   // straddles it
    [InlineData(-3, 200)] // straddles it and takes several rows with it
    public void DeleteAcrossAnAnchorBoundary_MatchesAFreshIndex(int startNudge, int length)
    {
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = AnchorRowStart(document, 4) + startNudge;

        document.Delete(at, length);

        document.AssertMatchesAFreshIndex($"delete {length} from anchor 4 {startNudge:+0;-0;+0}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ReplaceAcrossAnAnchorBoundary_MatchesAFreshIndex(int startNudge)
    {
        // Same length in and out, so nothing moves - but the bytes a forced break backs off from
        // do change, which is the one way a zero-delta edit can still re-flow rows.
        var document = LongDocument(2_000, wrapWidth: 40);
        long at = AnchorRowStart(document, 4) + startNudge;

        document.Replace(at, 6, Bytes("éé\nZ"));

        document.AssertMatchesAFreshIndex($"replace at anchor 4 {startNudge:+0;-0;+0}");
    }

    [Fact]
    public void EditsEitherSideOfAnAnchorBoundary_ShareOneSpan()
    {
        var document = LongDocument(2_000, wrapWidth: 40);
        long boundary = AnchorRowStart(document, 4);

        document.Insert(boundary - 2, Bytes("a"));
        document.Insert(boundary + 3, Bytes("b"));

        // Both are inside the same anchor bucket's reach, so there is nothing for a second span
        // to describe that the first does not already cover.
        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("edits either side of an anchor boundary");
    }

    [Fact]
    public void EditsInTheSameAnchorBucket_MustShareOneSpan()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(AnchorRowStart(document, 4), Bytes("a"));

        // The first insert shifted everything after it by a byte, so this offset - taken from
        // the original index - now maps back into anchor bucket 4, the one the first span has
        // already re-derived past. Both reasons to widen apply here; what is pinned is that the
        // result is one span, because the cost of getting this particular case wrong is two
        // overlapping spans and therefore wrong answers, not merely a wasteful one.
        document.Insert(AnchorRowStart(document, 5), Bytes("b"));

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("second edit inside the first span's anchor bucket");
    }

    [Fact]
    public void AnEditWithinAStrideOfTheLastSpan_WidensItRatherThanOpeningAnother()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(AnchorRowStart(document, 4), Bytes("a"));

        // Far enough in that it genuinely belongs to the next anchor bucket - so the spans would
        // not overlap - but still under one stride from where the first span re-converged, which
        // is the point at which widening costs less than a new span's own anchor walk.
        document.Insert(AnchorRowStart(document, 5) + 32, Bytes("b"));

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("second edit one anchor on");
    }

    [Fact]
    public void AnEditMoreThanAStrideAway_OpensItsOwnSpan()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(AnchorRowStart(document, 4), Bytes("a"));
        document.Insert(AnchorRowStart(document, 6) + 32, Bytes("b"));

        Assert.Equal(2, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("second edit two anchors on");
    }

    [Fact]
    public void EditsSeveralAnchorsApart_OpenSeparateSpans()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(AnchorRowStart(document, 4), Bytes("a"));
        document.Insert(AnchorRowStart(document, 12), Bytes("b"));

        Assert.Equal(2, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("edits eight anchors apart");
    }

    [Fact]
    public void EditingBackwardsAcrossAnchors_KeepsTheSpansInOrder()
    {
        // Opening spans out of order is the case where an insert into the middle of the list has
        // to renumber every span after it; doing it in reverse guarantees that path runs.
        var document = LongDocument(2_000, wrapWidth: 40);

        foreach (int anchorIndex in new[] { 20, 14, 8, 2 })
        {
            document.Insert(AnchorRowStart(document, anchorIndex), Bytes("z"));
            document.AssertMatchesAFreshIndex($"inserted at anchor {anchorIndex}");
        }

        Assert.Equal(4, document.Rows.SpanCount);
    }

    [Fact]
    public void ADeleteThatSwallowsAnotherSpan_AbsorbsIt()
    {
        var document = LongDocument(2_000, wrapWidth: 40);

        long first = AnchorRowStart(document, 4);
        long second = AnchorRowStart(document, 12);
        document.Insert(first, Bytes("a"));
        document.Insert(second, Bytes("b"));
        Assert.Equal(2, document.Rows.SpanCount);

        // A delete running from the first edit past the second: the second span's rows no longer
        // exist, and its byte delta has to survive inside the first span or the document's total
        // delta stops adding up.
        document.Delete(first, (int)(second + 1 - first) + 20);

        Assert.Equal(1, document.Rows.SpanCount);
        document.AssertMatchesAFreshIndex("delete swallowing the span after it");
    }

    [Fact]
    public void AnEditInsideAVeryLongLine_HoldsAnchorsRatherThanEveryRow()
    {
        // The case that came out of running the editor on a 4GB document: a 48-byte edit inside
        // a ~54MB unbroken line produced a span of 676,661 rows and 21MB of RawRowInfo, which was
        // ten times the budget and refused every further edit anywhere in the file.
        //
        // Why the whole line has to be walked at all is in the class remarks: the appealing
        // shortcut - "a soft-wrapped line breaks every WrapWidth bytes, so an insert leaves the
        // later breaks where they were" - is untrue, because a forced break backs off up to 3
        // bytes to avoid splitting a character and that chains. What is avoidable is keeping
        // every row the walk passes.
        const int WrapWidth = 80;
        var text = new StringBuilder();
        text.Append(new string('a', 400_000)).Append('\n');
        for (int i = 0; i < 200; i++)
            text.Append($"line {i}\n");

        var document = new EditedDocument(Bytes(text.ToString()), WrapWidth);
        document.Insert(100, Bytes("EDIT"));

        var span = Assert.Single(EnumerateSpans(document));
        Assert.True(span.RowsHeld > 4_000, $"the walk should have covered the line; it covered {span.RowsHeld} rows");
        Assert.Equal((span.RowsHeld + RawSegmentIndexAnchorStride - 1) / RawSegmentIndexAnchorStride, span.AnchorsHeld);
        Assert.False(document.Rows.NeedsRebuild);

        document.AssertMatchesAFreshIndex("one edit inside a 400KB line");
    }

    [Fact]
    public void RowsDeepInsideALongLinesSpan_AreRecoveredFromTheNearestAnchor()
    {
        // The anchors are only worth having if a row between two of them still comes back
        // correct, so this reads every row of the span rather than only its edges - the oracle
        // above does too, but this one says out loud which mechanism it is exercising.
        var text = new StringBuilder();
        text.Append(new string('b', 60_000)).Append('\n');
        text.Append("after\n");

        var document = new EditedDocument(Bytes(text.ToString()), 80);
        document.Insert(40, Bytes("Z"));

        document.AssertMatchesAFreshIndex("every row of a long line's span");
    }

    private static IEnumerable<RawSpanSnapshot> EnumerateSpans(EditedDocument document)
        => document.Rows.DescribeSpans();

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
    public void AnEditLateInALongLinesSpan_ResumesTheWalkRatherThanRestartingIt()
    {
        // Rows before the earliest byte an edit touched cannot have moved, and their anchors are
        // stored relative to the span's start, so the walk picks up at the last one the edit
        // could not have disturbed. Without it, typing at the end of a long line costs the same
        // as typing at its start - which on a 4GB document is the difference between instant and
        // a visible stutter on every keystroke.
        var (document, lineEnd) = LongLineDocument(400_000);

        document.Insert(100, Bytes("x"));
        long fromTheStart = document.Rows.RowsWalkedInLastEdit;

        document.Insert(lineEnd - 500, Bytes("y"));
        long fromNearTheEnd = document.Rows.RowsWalkedInLastEdit;

        Assert.True(fromTheStart > 4_000, $"the first edit should have walked the line; it walked {fromTheStart}");
        Assert.True(fromNearTheEnd < fromTheStart / 100,
            $"resumed walk covered {fromNearTheEnd} rows against {fromTheStart} for the whole line");

        document.AssertMatchesAFreshIndex("edits at both ends of a long line");
    }

    [Fact]
    public void AnEditPastALargeSpan_OpensItsOwnSpanRatherThanWideningIt()
    {
        // Widening re-walks the whole span, so "near enough to widen" has to weigh what widening
        // would cost. Next to a span covering a long line it never pays, and treating it as
        // though it did is what made an edit just past such a line as slow as one inside it.
        var (document, lineEnd) = LongLineDocument(400_000);

        document.Insert(100, Bytes("x"));
        Assert.Equal(1, document.Rows.SpanCount);

        // Two anchor buckets past where the long line's span rejoined the original - close
        // enough that the old rule would have widened it.
        document.Insert(lineEnd + 2_000, Bytes("y"));

        Assert.Equal(2, document.Rows.SpanCount);
        Assert.True(document.Rows.RowsWalkedInLastEdit < 500,
            $"opening a span should cost an anchor bucket; it walked {document.Rows.RowsWalkedInLastEdit} rows");

        document.AssertMatchesAFreshIndex("edit past a long line's span");
    }

    [Fact]
    public void AnEditBesideASmallSpan_StillWidensIt()
    {
        // The counterpart: widening is still right when the span is small, which is every
        // ordinary edit. Otherwise this change would trade one cost for span proliferation.
        var document = LongDocument(2_000, wrapWidth: 40);

        document.Insert(AnchorRowStart(document, 4), Bytes("a"));
        document.Insert(AnchorRowStart(document, 5) + 32, Bytes("b"));

        Assert.Equal(1, document.Rows.SpanCount);
    }

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
