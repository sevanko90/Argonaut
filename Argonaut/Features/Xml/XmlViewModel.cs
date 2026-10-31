using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Hints;
using Argonaut.Features.Json.Preview;
using Argonaut.Ui.Hints;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Search;
using Argonaut.Engine.Text;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;
using Argonaut.Ui.Launching;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Xml;

/// <summary>
/// An XML document shown as a tree. The tree reads the bytes through a sparse index
/// (<see cref="XmlSparseIndex"/>) and a <see cref="TreeDocument"/> the view's surface draws;
/// nothing holds a record per node, and the tree can be shown before indexing has got anywhere.
/// Paths are XPaths spelled with the document's own prefixes.
/// </summary>
public sealed class XmlViewModel : TreeDocumentViewModel
{
    /// <summary>The most of a text row's content copy-value puts on the clipboard, in bytes.</summary>
    private const int CopyValueCap = 64 * 1024;

    private const string CDataOpen = "<![CDATA[";
    private const string CDataClose = "]]>";

    private XmlTreeReader? reader;
    private XmlTreeText? text;
    private XmlToolbarViewModel? toolbar;

    // What the whole-document index was built over, so a finished one can be kept on the origin
    // for the next XML view of it (see KeptIndexes). Null while the origin's bytes are still arriving.
    private IndexBasis? indexBasis;

    // "declared ISO-8859-1, shown as UTF-8" when the XML declaration names another encoding.
    private string? encodingNote;

    private IReadOnlyList<IValueHintProvider>? hintProviders;

    public XmlViewModel(XmlViewSettings viewSettings, ISystemLauncher? launcher = null) : base(viewSettings, launcher)
    {
        HintSettings.HintsChanged += OnHintsChanged;
    }

    /// <summary>The date-hint choices for this document - how a number reads as a date, and the
    /// time zone shown. XML text needs no default scheme inferred: its dates are ISO.</summary>
    public DateHintSettings HintSettings { get; } = new();

    private void OnHintsChanged(object? sender, EventArgs e) => InvalidateRows();

    /// <summary>The sparse index, once loading has started.</summary>
    internal XmlSparseIndex? Index => (XmlSparseIndex?)TreeSession?.Index;

    /// <summary>This document's header toolbar (see <see cref="IDocumentViewModel.Toolbar"/>).
    /// Null until LoadAsync creates it.</summary>
    public override XmlToolbarViewModel? Toolbar => toolbar;

    public Task LoadAsync(IByteOrigin origin, IProgressReporter? progressReporter = null)
    {
        AdoptOrigin(origin, new ScanTarget(origin));
        DefaultExpandDepth = ViewSettings.ExpandDepth;
        toolbar = new XmlToolbarViewModel(HintSettings, DefaultExpandDepth, ChooseExpandDepth, ShowIndentGuides, ChooseIndentGuides);

        indexBasis = IndexBasis.Of(origin);
        return LoadCore(origin.Open(), progressReporter, indexBasis?.FindKept<XmlKeptStructure>(XmlKeptStructure.Key));
    }

    /// <summary>
    /// Loads the sub-document occupying the byte range [offset, offset + length) of
    /// <paramref name="origin"/>. Asks the origin for its own independent sub-range source
    /// (released with this view model).
    /// </summary>
    public Task LoadAsync(IByteOrigin origin, long offset, long length, IProgressReporter? progressReporter = null)
    {
        AdoptOrigin(origin, new ScanTarget(origin, offset, length));
        return LoadCore(origin.OpenRange(offset, length), progressReporter, kept: null);
    }

    /// <param name="kept">The structure a previous XML view of the same bytes kept, if any - a
    /// save from the text view, or another program's edit, means there is none.</param>
    private Task LoadCore(IByteSource bytes, IProgressReporter? progressReporter, XmlKeptStructure? kept)
    {
        var session = StartSession(bytes,
            (source, progress, stopping) => XmlSparseIndex.StartIndexing(source, kept, progress, stopping), progressReporter);

        reader = new XmlTreeReader(session.Bytes);
        text = new XmlTreeText(session.Bytes, session.Index.Structure, reader);
        hintProviders = ValueHintProviders.Standard(HintSettings, TimeProvider.System);
        ShowTree(reader, new XmlTreePainter(text, hintProviders), []);

        string? declared = XmlDeclaredEncoding.Read(session.Bytes);
        encodingNote = XmlDeclaredEncoding.IsNotUtf8(declared) ? $"declared {declared}, shown as UTF-8" : null;

        StatusDetail = Status("indexing…");
        MonitorIndexing();
        return Task.CompletedTask;
    }

    public override ISearchNavigator CreateSearchNavigator() => new TreeSearchNavigator(this);

    public override bool CanHandleFileType(FileTypeDetector.FileKind fileType) => fileType == FileTypeDetector.FileKind.Xml;

    /// <summary>The status detail: <paramref name="detail"/>, then the encoding note
    /// when the declaration names one that is not shown as it says.</summary>
    private string Status(string detail)
        => encodingNote is null ? detail : $"{detail} — {encodingNote}";

    protected override void OnIndexingCompleted()
    {
        indexBasis?.Keep(XmlKeptStructure.Key, Index?.DetachStructure());
        StatusDetail = Status(ByteLengthText.Format(Bytes?.AvailableLength ?? 0));
        Tree?.NotifyGrew();
    }

    /// <summary>Indexing stopped early (failure, or cancellation on <paramref name="failure"/> null).
    /// A document that is not well-formed has still been scanned to its end; the first thing that
    /// broke it is reported.</summary>
    protected override void OnIndexingFailed(IndexFailure? failure)
    {
        IndexFailure = failure;
        string detail = failure is { } f
            ? $"{f.Message} at line {f.Line?.ToString("N0") ?? "?"}, column {f.Column?.ToString("N0") ?? "?"}"
            : "indexing failed";
        StatusDetail = Status(detail);
    }

    /// <summary>
    /// What a hint's value encodes, ready to show: the text's bytes are copied out, their entity
    /// and character references decoded and the result escaped as a JSON string's content, which
    /// is the form the decoders read, and decoded off the UI thread, so nothing reads the
    /// document from another thread. Null when it does not decode after all, or the document
    /// closed meanwhile.
    /// </summary>
    public async Task<HintPreview?> PreviewHintAsync(ExpandHintLink link)
    {
        if (Bytes is not { } bytes)
            return null;

        long length = link.ValueEnd - link.ValueStart;
        if (HintPreviews.TooLarge(length) is { } tooLarge)
            return tooLarge;

        var written = bytes.RequireContiguous(link.ValueStart, (int)length);
        var decoded = XmlUnescape.Decode(written, new byte[written.Length]);
        byte[] raw = new byte[XmlHintText.EscapedLength(decoded)];
        XmlHintText.Escaped(decoded, raw).CopyTo(raw);
        var shown = await HintPreviews.DecodeAsync(link.Kind, raw, hintProviders, HintSettings.TimeZoneMode);
        if (IsDisposed)
        {
            shown?.Preview.Dispose();
            return null;
        }

        return shown;
    }

    protected override IReadOnlyList<TreePathSegment> PathSegmentsOf(TreeCursor cursor) => XmlTreePaths.Segments(cursor, text!, Index!);

    protected override string FormatPath(IReadOnlyList<TreePathSegment> segments) => XmlTreePaths.Format(segments);

    protected override TreePathResult ResolvePath(SparseContainerIndex structure, string path)
        => XmlTreePaths.Resolve(Index!, reader!, text!, path);

    /// <summary>
    /// A text row's, CDATA row's or text-only element's text as written (not folded onto one line
    /// as the row shows it), capped; anything else as the row's own markup - a container's start
    /// tag, a comment whole.
    /// </summary>
    protected override string ValueTextOf(in TreeRow row)
    {
        var node = row.Node;
        long start = node.ValueStart;
        long end = node.ValueEnd;
        switch ((XmlNodeKind)node.FormatKind)
        {
            case XmlNodeKind.TextElement:
                text!.StartTag(start, out long tagEnd, out _);
                return Trimmed(tagEnd, text.CloseStart(start, end));

            case XmlNodeKind.Text:
                return Trimmed(start, end);

            case XmlNodeKind.CData:
                bool closed = end - start >= CDataOpen.Length + CDataClose.Length
                              && Bytes!.GetUtf8String(end - CDataClose.Length, CDataClose.Length) == CDataClose;
                return Read(start + CDataOpen.Length, closed ? end - CDataClose.Length : end);

            case XmlNodeKind.Element:
                return text!.StartTag(start, out _, out _);

            default:
                return text!.Markup(start, end, out _);
        }
    }

    /// <summary>The text between the offsets with the whitespace at both ends left out.</summary>
    private string Trimmed(long start, long end)
    {
        text!.Content(start, end, out _, out long contentOffset, out long fullLength);
        return Read(contentOffset, contentOffset + fullLength);
    }

    private string Read(long start, long end)
        => DisplayText.Read(Bytes!, start, (int)System.Math.Min(int.MaxValue, end - start), out _, CopyValueCap);
}
