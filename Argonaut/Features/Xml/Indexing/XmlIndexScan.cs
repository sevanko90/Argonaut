using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;

namespace Argonaut.Features.Xml.Indexing;

/// <summary>
/// One pass over a document that turns its markup into <see cref="SparseContainerIndexBuilder"/>
/// events and the sibling counts beside them. It holds all the state of the pass - the stack of
/// open elements, the names on it, the counts - so none of it outlives the scan.
///
/// The pass is a resumable state machine fed span by span: text, then the construct a <c>&lt;</c>
/// opens, read to its end by the <see cref="XmlStructuralScanner"/> primitives, which carry their
/// state between spans. Nothing it reads is required whole, so it gives the same index over a
/// mapping, a source split into pieces of one byte, and one still receiving.
///
/// Whether an element is a container is known only at its first child markup, so a start tag is
/// held, not opened. If the next markup inside it is its own end tag it held only text and CDATA
/// (or nothing) and is a leaf the index never records, however long its text. If it is an element,
/// a comment or a processing instruction, the element is opened then, before that child's events.
/// The scan position published while an element is held stops at its start, so it never passes a
/// container the index has not opened.
/// </summary>
internal sealed class XmlIndexScan
{
    /// <summary>Bytes asked of the source per turn of the scan loop. Bounds how often progress,
    /// cancellation and the published scan position are updated.</summary>
    private const int ReadStride = 256 * 1024;

    private const long ProgressReportStride = 4 * 1024 * 1024;

    /// <summary>The longest element name or processing instruction target the scan reads; a longer
    /// one is a failure, and the name is cut at this length.</summary>
    private const int MaxNameBytes = 4096;

    private enum ChildKind
    {
        /// <summary>A start tag: decides that its parent is a container.</summary>
        Element,

        /// <summary>A comment, processing instruction or DOCTYPE: decides it too.</summary>
        Markup,

        /// <summary>Text or CDATA: does not decide.</summary>
        Text,
    }

    private enum Construct
    {
        Tag,
        CloseTag,
        Comment,
        CData,
        ProcessingInstruction,
        Doctype,
    }

    private struct OpenElement
    {
        public long Start;
        public long Children;
        public int NameOffset;
        public int NameLength;
        public int KeyCount;
        public bool Opened;
        public bool TooManyKeys;
    }

    private readonly IByteSource source;
    private readonly SparseContainerIndex structure;
    private readonly SparseContainerIndexBuilder builder;
    private readonly XmlSiblingCounts siblingCounts;
    private readonly IProgressReporter? progressReporter;
    private readonly CancellationToken stopping;

    private ReadAhead readAhead;
    private long nextReport = ProgressReportStride;

    // Everything before this is accounted for.
    private long position;

    // The first non-whitespace byte of the text run being read, or -1.
    private long textStart = -1;

    // The start of a start tag still being read, or -1: it may yet be a container's first
    // child, so the published position stays behind it.
    private long tagInProgress = -1;

    private OpenElement[] open = new OpenElement[64];
    private int depth;

    // The names of the open elements, one after another; an element's is at NameOffset.
    private byte[] names = new byte[1024];
    private int namesUsed;

    // Per open element, up to MaxDistinctKeys child keys and their counts, in a slice by depth.
    private ulong[] keyTable = new ulong[64 * XmlSiblingCounts.MaxDistinctKeys];
    private long[] countTable = new long[64 * XmlSiblingCounts.MaxDistinctKeys];

    // The starts of the text and CDATA children of the held element: they are replayed as
    // separators when it opens, and dropped when it turns out to be a leaf.
    private readonly List<long> pendingText = [];

    private readonly byte[] lookScratch = new byte[MaxNameBytes + 2];

    private long topLevelChildren;
    private bool rootSeen;

    private string? failureMessage;
    private long failureOffset;
    private long failureLine;
    private long failureColumn;

    public XmlIndexScan(IByteSource source, SparseContainerIndex structure, XmlSiblingCounts siblingCounts,
        IProgressReporter? progressReporter, CancellationToken stopping)
    {
        this.source = source;
        this.structure = structure;
        builder = new SparseContainerIndexBuilder(structure);
        this.siblingCounts = siblingCounts;
        this.progressReporter = progressReporter;
        this.stopping = stopping;
    }

    /// <summary>How far the scan has read; where a crash in it happened.</summary>
    public long Position => position;

    /// <summary>The first thing wrong with the document, in document order, once the pass is
    /// done; null if it is well-formed as far as structure goes.</summary>
    public IndexFailure? Failure => failureMessage is null
        ? null
        : new IndexFailure(failureMessage, failureOffset, failureLine, failureColumn, structure.ContainerCount);

    /// <summary>Reads the whole document, waiting for bytes that have not arrived, and completes
    /// the structure once the length is settled and everything is closed. Carries on past what it
    /// cannot make sense of; <see cref="Failure"/> says what the first of those was.</summary>
    public void Run()
    {
        SkipByteOrderMark();

        while (true)
        {
            stopping.ThrowIfCancellationRequested();

            // Settled first, then the length: read the other way round, a source that receives its
            // last bytes and settles in between would be finished with those bytes unread. Both
            // are re-read every turn, never snapshotted (see IByteSource.AvailableLength).
            bool settled = source.LengthSettled;
            long available = source.AvailableLength;
            if (position >= available)
            {
                if (settled)
                    break;

                PublishPosition();
                source.WaitForLength(position + 1, stopping);
                continue;
            }

            readAhead.Reached(source, position);
            var span = source.GetContiguousSpan(position, (int)Math.Min(ReadStride, available - position));
            if (span.IsEmpty)
                throw new InvalidOperationException($"The source returned no bytes at {position}, below its available length of {available}.");

            int markup = span.IndexOf((byte)'<');
            var text = markup < 0 ? span : span.Slice(0, markup);
            if (textStart < 0 && !text.IsEmpty)
            {
                int content = text.IndexOfAnyExcept(XmlStructuralScanner.Whitespace);
                if (content >= 0)
                {
                    textStart = position + content;
                    BeginText(textStart);
                }
            }

            if (markup < 0)
            {
                position += span.Length;
            }
            else
            {
                position += markup;
                textStart = -1;
                Markup();
            }

            PublishPosition();
            ReportProgress(available);
        }

        // Elements still open end where the data does: the index is complete, and nothing has to
        // scan to the end of the file to find where they stop.
        long end = position;
        if (depth > 0)
        {
            if (failureMessage is null)
                Fail(end, $"The document ends with {depth} element(s) still open; <{NameOf(depth - 1)}> was never closed.");

            while (depth > 0)
                CloseTop(end);
        }

        builder.Complete(end);
        progressReporter?.Report("Indexing", end, end);
    }

    /// <summary>A UTF-8 byte order mark is not text outside the root.</summary>
    private void SkipByteOrderMark()
    {
        var head = LookWaiting(0, 3);
        if (head.StartsWith("﻿"u8))
            position = 3;
    }

    private void Markup()
    {
        long start = position;
        var head = LookWaiting(start, 2);
        if (head.Length < 2)
        {
            // A "<" that is the last byte of the document.
            if (failureMessage is null)
                Fail(start, "The document ends inside a tag.");
            position = start + head.Length;
            return;
        }

        byte next = head[1];
        if (next == (byte)'/')
        {
            EndTag(start);
        }
        else if (next == (byte)'?')
        {
            ProcessingInstruction(start);
        }
        else if (next == (byte)'!')
        {
            Declaration(start);
        }
        else if (IsNameStart(next))
        {
            StartTag(start);
        }
        else
        {
            // Not markup at all; carry on as if it were text.
            if (failureMessage is null)
                Fail(start, "A '<' does not start a tag, comment, CDATA section or processing instruction.");
            position = start + 1;
        }
    }

    private void Declaration(long start)
    {
        var head = LookWaiting(start, 9);
        if (head.StartsWith("<!--"u8))
        {
            BeginChild(start, XmlSiblingCounts.CommentKey, ChildKind.Markup);
            ReadToEnd(Construct.Comment, start);
        }
        else if (head.StartsWith("<![CDATA["u8))
        {
            if (depth == 0)
            {
                if (failureMessage is null)
                    Fail(start, "A CDATA section outside the root element.");
            }
            else
            {
                BeginChild(start, XmlSiblingCounts.TextKey, ChildKind.Text);
            }

            ReadToEnd(Construct.CData, start);
        }
        else if (head.StartsWith("<!DOCTYPE"u8))
        {
            BeginChild(start, XmlSiblingCounts.CommentKey, ChildKind.Markup);
            ReadToEnd(Construct.Doctype, start);
        }
        else
        {
            if (failureMessage is null)
                Fail(start, "A '<!' does not start a comment, CDATA section or DOCTYPE.");
            position = start + 2;
        }
    }

    private void ProcessingInstruction(long start)
    {
        ulong key = 0;
        var target = ReadName(start + 2, out bool tooLong);
        if (tooLong && failureMessage is null)
            Fail(start, "A processing instruction's target is longer than 4 KB.");
        if (depth > 0 && !open[depth - 1].TooManyKeys)
            key = XmlSiblingCounts.ProcessingInstructionKeyOf(target);

        BeginChild(start, key, ChildKind.Markup);
        ReadToEnd(Construct.ProcessingInstruction, start);
    }

    private void StartTag(long start)
    {
        var name = ReadName(start + 1, out bool tooLong);
        if (tooLong && failureMessage is null)
            Fail(start, "An element name is longer than 4 KB.");

        // The name is kept past the next read of the source, which may reuse what it came from,
        // in the free space after the open elements' names.
        EnsureNames(namesUsed + name.Length);
        name.CopyTo(names.AsSpan(namesUsed));
        int nameLength = name.Length;
        ulong key = depth > 0 && !open[depth - 1].TooManyKeys ? XmlSiblingCounts.KeyOf(name) : 0;

        BeginChild(start, key, ChildKind.Element);

        tagInProgress = start;
        bool found = ReadToEnd(Construct.Tag, start);
        tagInProgress = -1;
        if (!found || XmlStructuralScanner.IsSelfClosing(source, position))
            return;

        Push(start, nameLength);
    }

    private void EndTag(long start)
    {
        var name = ReadName(start + 2, out bool tooLong);
        if (tooLong && failureMessage is null)
            Fail(start, "An end tag's name is longer than 4 KB.");

        // Held in the free space after the open names, where an element would be pushed.
        EnsureNames(namesUsed + name.Length);
        name.CopyTo(names.AsSpan(namesUsed));
        int nameLength = name.Length;

        if (!ReadToEnd(Construct.CloseTag, start))
            return;

        long end = position;
        var closing = names.AsSpan(namesUsed, nameLength);

        if (depth == 0)
        {
            if (failureMessage is null)
                Fail(start, $"A close tag </{Encoding.UTF8.GetString(closing)}> has nothing to close.");
            return;
        }

        if (NameIs(depth - 1, closing))
        {
            CloseTop(end);
            return;
        }

        if (failureMessage is null)
            Fail(start, $"The close tag </{Encoding.UTF8.GetString(closing)}> does not match the open <{NameOf(depth - 1)}>.");

        // Recover as a reader of the file would: an element further up with this name is closed,
        // and everything inside it with it. With none, the close tag is stray and skipped, so one
        // missing or extra tag does not nest the rest of the file a level deeper.
        for (int up = depth - 2; up >= 0; up--)
        {
            if (!NameIs(up, closing))
                continue;

            while (depth > up)
                CloseTop(end);
            return;
        }
    }

    private void BeginText(long start)
    {
        if (depth == 0)
        {
            if (failureMessage is null)
                Fail(start, "Text outside the root element.");
            return;
        }

        BeginChild(start, XmlSiblingCounts.TextKey, ChildKind.Text);
    }

    /// <summary>
    /// A child of the innermost open element, or of the document, starts at
    /// <paramref name="start"/>. Everything except the first child follows a separator. A child
    /// that is not text decides that a held parent is a container, so the parent opens first; text
    /// does not, so it waits in <see cref="pendingText"/> to be replayed if the parent opens.
    /// </summary>
    private void BeginChild(long start, ulong key, ChildKind kind)
    {
        if (depth == 0)
        {
            if (kind == ChildKind.Element)
            {
                if (rootSeen && failureMessage is null)
                    Fail(start, "A second root element: a document has only one.");
                rootSeen = true;
            }

            if (topLevelChildren++ > 0)
                builder.Separator(start);
            return;
        }

        ref var parent = ref open[depth - 1];
        if (!parent.Opened)
        {
            if (kind == ChildKind.Text)
            {
                pendingText.Add(start);
                parent.Children++;
                return;
            }

            OpenHeld(depth - 1);
            parent = ref open[depth - 1];
        }

        if (parent.Children > 0)
            Separate(depth - 1, start);

        parent.Children++;
        CountChild(depth - 1, key);
    }

    /// <summary>The held element at <paramref name="index"/> turns out to be a container: opens
    /// it, then replays the text and CDATA children it has had so far.</summary>
    private void OpenHeld(int index)
    {
        ref var element = ref open[index];
        builder.Open(element.Start, (byte)XmlNodeKind.Element);
        element.Opened = true;
        element.KeyCount = 0;
        element.TooManyKeys = false;

        for (int i = 0; i < pendingText.Count; i++)
        {
            if (i > 0)
                Separate(index, pendingText[i]);
            CountChild(index, XmlSiblingCounts.TextKey);
        }

        pendingText.Clear();
    }

    /// <summary>A child other than the first starts in the open element at
    /// <paramref name="index"/>. When that adds a checkpoint, the counts before it are stored
    /// beside it.</summary>
    private void Separate(int index, long start)
    {
        int checkpoints = structure.CheckpointCount;
        builder.Separator(start);
        if (structure.CheckpointCount == checkpoints)
            return;

        ref var element = ref open[index];
        int table = index * XmlSiblingCounts.MaxDistinctKeys;
        siblingCounts.AddBeforeCheckpoint(
            keyTable.AsSpan(table, element.KeyCount), countTable.AsSpan(table, element.KeyCount), element.TooManyKeys);
    }

    private void CountChild(int index, ulong key)
    {
        ref var element = ref open[index];
        if (element.TooManyKeys)
            return;

        int table = index * XmlSiblingCounts.MaxDistinctKeys;
        int found = keyTable.AsSpan(table, element.KeyCount).IndexOf(key);
        if (found >= 0)
        {
            countTable[table + found]++;
        }
        else if (element.KeyCount < XmlSiblingCounts.MaxDistinctKeys)
        {
            keyTable[table + element.KeyCount] = key;
            countTable[table + element.KeyCount] = 1;
            element.KeyCount++;
        }
        else
        {
            element.TooManyKeys = true;
        }
    }

    private void Push(long start, int nameLength)
    {
        if (depth == open.Length)
        {
            Array.Resize(ref open, depth * 2);
            Array.Resize(ref keyTable, keyTable.Length * 2);
            Array.Resize(ref countTable, countTable.Length * 2);
        }

        open[depth++] = new OpenElement { Start = start, NameOffset = namesUsed, NameLength = nameLength };
        namesUsed += nameLength;
    }

    /// <summary>Closes the innermost element at <paramref name="end"/>. A held one is a leaf and
    /// leaves no trace; an opened one closes in the builder, and its counts are stored if it was
    /// recorded.</summary>
    private void CloseTop(long end)
    {
        ref var element = ref open[depth - 1];
        if (element.Opened)
        {
            builder.Close(end, isEmpty: false);
            if (end - element.Start >= structure.PromotionBytes)
            {
                int container = structure.FindContainerStartingAt(element.Start);
                if (container >= 0)
                {
                    int table = (depth - 1) * XmlSiblingCounts.MaxDistinctKeys;
                    siblingCounts.SetTotals(container, keyTable.AsSpan(table, element.KeyCount),
                        countTable.AsSpan(table, element.KeyCount), element.TooManyKeys);
                }
            }
        }
        else
        {
            pendingText.Clear();
        }

        namesUsed = element.NameOffset;
        depth--;
    }

    /// <summary>
    /// Reads the construct that began at <paramref name="start"/> to its end, waiting for bytes
    /// that have not arrived, and moves <see cref="position"/> past it. A construct that is never
    /// closed fails, and the scan ends with it, since it runs to the end of the data.
    /// </summary>
    private bool ReadToEnd(Construct construct, long start)
    {
        var resume = construct switch
        {
            Construct.Tag => XmlScanResume.Tag(start),
            Construct.CloseTag => XmlScanResume.CloseTag(start),
            Construct.Comment => XmlScanResume.Comment(start),
            Construct.CData => XmlScanResume.CData(start),
            Construct.ProcessingInstruction => XmlScanResume.ProcessingInstruction(start),
            _ => XmlScanResume.Doctype(start),
        };

        while (true)
        {
            stopping.ThrowIfCancellationRequested();

            long end;
            var outcome = construct switch
            {
                Construct.Tag => XmlStructuralScanner.TagEnd(source, ref resume, out end),
                Construct.CloseTag => XmlStructuralScanner.CloseTagEnd(source, ref resume, out end),
                Construct.Comment => XmlStructuralScanner.CommentEnd(source, ref resume, out end),
                Construct.CData => XmlStructuralScanner.CDataEnd(source, ref resume, out end),
                Construct.ProcessingInstruction => XmlStructuralScanner.ProcessingInstructionEnd(source, ref resume, out end),
                _ => XmlStructuralScanner.DoctypeEnd(source, ref resume, out end),
            };

            switch (outcome)
            {
                case XmlScanOutcome.Found:
                    position = end;
                    return true;

                case XmlScanOutcome.Unterminated:
                    if (failureMessage is null)
                        Fail(start, UnterminatedMessage(construct));
                    position = end;
                    return false;

                default:
                    position = end;
                    PublishPosition();
                    ReportProgress(end);
                    source.WaitForLength(end + 1, stopping);
                    break;
            }
        }
    }

    private static string UnterminatedMessage(Construct construct) => construct switch
    {
        Construct.Comment => "The comment that starts here is never closed: no --> before the end of the document.",
        Construct.CData => "The CDATA section that starts here is never closed: no ]]> before the end of the document.",
        Construct.ProcessingInstruction => "The processing instruction that starts here is never closed: no ?> before the end of the document.",
        Construct.Doctype => "The DOCTYPE that starts here is never closed: no > before the end of the document.",
        _ => "The tag that starts here is never closed: no > before the end of the document.",
    };

    /// <summary>
    /// An element name or processing-instruction target starting at <paramref name="offset"/>:
    /// the bytes up to whitespace, <c>/</c>, <c>&gt;</c> or <c>?</c>, read from wherever the
    /// source has them. Valid until the next read of the source, which may reuse its storage.
    /// </summary>
    private ReadOnlySpan<byte> ReadName(long offset, out bool tooLong)
    {
        tooLong = false;
        int wanted = 64;
        while (true)
        {
            var read = Available(offset, wanted, out bool settled);
            int end = read.IndexOfAny(XmlStructuralScanner.NameEnd);
            if (end >= 0)
                return read.Slice(0, end);

            if (read.Length < wanted)
            {
                // The name runs to the end of what has arrived: the end of the document, or all
                // there is yet.
                if (settled)
                    return read;

                PublishPosition();
                source.WaitForLength(offset + read.Length + 1, stopping);
                continue;
            }

            if (wanted > MaxNameBytes)
            {
                tooLong = true;
                return read.Slice(0, MaxNameBytes);
            }

            wanted = Math.Min(wanted * 8, MaxNameBytes + 1);
        }
    }

    /// <summary>Up to <paramref name="wanted"/> bytes from <paramref name="offset"/>, as many as are
    /// there now: the source's own memory when it holds them in one piece, else a copy.</summary>
    private ReadOnlySpan<byte> Available(long offset, int wanted, out bool settled)
    {
        settled = source.LengthSettled;
        long here = Math.Min(wanted, Math.Max(0, source.AvailableLength - offset));
        if (here == 0)
            return default;

        var span = source.GetContiguousSpan(offset, (int)here);
        if (span.Length >= here)
            return span;

        int copied = source.CopyTo(offset, lookScratch.AsSpan(0, (int)here));
        return lookScratch.AsSpan(0, copied);
    }

    /// <summary><see cref="Available"/>, but waiting until <paramref name="wanted"/> bytes have
    /// arrived or the length is settled.</summary>
    private ReadOnlySpan<byte> LookWaiting(long offset, int wanted)
    {
        while (true)
        {
            var read = Available(offset, wanted, out bool settled);
            if (read.Length >= wanted || settled)
                return read;

            PublishPosition();
            source.WaitForLength(offset + wanted, stopping);
        }
    }

    private void PublishPosition()
    {
        long limit = position;
        if (tagInProgress >= 0)
            limit = Math.Min(limit, tagInProgress);
        else if (depth > 0 && !open[depth - 1].Opened)
            limit = Math.Min(limit, open[depth - 1].Start);

        builder.Advance(limit);
    }

    private void ReportProgress(long available)
    {
        if (position < nextReport)
            return;

        progressReporter?.Report("Indexing", position, available);
        while (nextReport <= position)
            nextReport += ProgressReportStride;
    }

    private void EnsureNames(int needed)
    {
        if (needed > names.Length)
            Array.Resize(ref names, Math.Max(needed, names.Length * 2));
    }

    private bool NameIs(int index, ReadOnlySpan<byte> name)
        => names.AsSpan(open[index].NameOffset, open[index].NameLength).SequenceEqual(name);

    private string NameOf(int index)
        => Encoding.UTF8.GetString(names, open[index].NameOffset, open[index].NameLength);

    private static bool IsNameStart(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
        or (byte)'_' or (byte)':' or >= 0x80;

    /// <summary>Records the first failure only, with its line and column counted from the start of
    /// the document - a scan over a possibly multi-GB document, made once, and only for a
    /// document that is already known to be broken.</summary>
    private void Fail(long offset, string message)
    {
        failureMessage = message;
        failureOffset = offset;

        long line = 1;
        long lineStart = 0;
        long at = 0;
        while (at < offset)
        {
            var span = source.GetContiguousSpan(at, (int)Math.Min(1 << 20, offset - at));
            if (span.IsEmpty)
                break;

            int lines = span.Count((byte)'\n');
            if (lines > 0)
            {
                line += lines;
                lineStart = at + span.LastIndexOf((byte)'\n') + 1;
            }

            at += span.Length;
        }

        failureLine = line;
        failureColumn = offset - lineStart + 1;
    }
}
