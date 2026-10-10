using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Preview;

/// <summary>
/// One value shown in full, away from the row it sits on: the array table's cell pane, and the
/// card a hint opens into what it encodes. Exactly one of four things - a JSON tree over its own
/// bytes, text, a picture, or a message saying why there is nothing to show here.
///
/// A tree is the JSON view's own - the same sparse index, <see cref="TreeDocument"/> and painter
/// over just the value's bytes - so a large one opens at once and holds nothing beyond the rows on
/// screen. Text is bounded, and cut where a run is too long to wrap cheaply.
/// </summary>
public sealed class ValuePreview : IDisposable
{
    /// <summary>
    /// Bytes of text decoded for display. Far past a row's own display cap - the point of a preview
    /// is to show what the row could not - while still bounded, because a value can be as large as
    /// the file, and the text is laid out whole.
    /// </summary>
    public const int MaxTextBytes = 64 * 1024;

    /// <summary>Longest run without whitespace handed to the wrapping text block; longer runs are
    /// cut into paragraphs (<see cref="DisplayText.BreakLongRuns"/>), which keeps a 64KB hash or
    /// Base64 value at tens of milliseconds to lay out instead of seconds.</summary>
    private const int MaxWrappableRun = 1024;

    /// <summary>Levels of a tree opened at first - enough to see its shape without reading a large
    /// subtree.</summary>
    private const int TreeExpandDepth = 2;

    private readonly IndexedSourceSession<JsonSparseIndex>? session;
    private readonly IByteOrigin? ownedOrigin;

    private ValuePreview(string title, string? text = null, bool truncated = false, TreeDocument? tree = null,
        byte[]? image = null, string? message = null, IndexedSourceSession<JsonSparseIndex>? session = null, IByteOrigin? ownedOrigin = null)
    {
        Title = title;
        Text = text;
        WrappableText = text is null ? null : DisplayText.BreakLongRuns(text, MaxWrappableRun);
        Truncated = truncated;
        Tree = tree;
        Image = image;
        Message = message;
        this.session = session;
        this.ownedOrigin = ownedOrigin;
    }

    /// <summary>What is shown: a cell's route and row, or what a hint decoded.</summary>
    public string Title { get; }

    /// <summary>The text exactly as it is (up to <see cref="MaxTextBytes"/>), which is what copying
    /// takes; null unless this is text.</summary>
    public string? Text { get; }

    /// <summary><see cref="Text"/> as shown, with newlines cutting any run too long to wrap
    /// cheaply - so selecting across one of those cuts copies a newline the value lacks.</summary>
    public string? WrappableText { get; }

    /// <summary>The text was longer than <see cref="MaxTextBytes"/> and is shown cut.</summary>
    public bool Truncated { get; }

    /// <summary>The JSON tree, or null when this is not one.</summary>
    public TreeDocument? Tree { get; }

    /// <summary>An encoded picture - PNG, JPEG, GIF or WebP - or null when this is not one.</summary>
    public byte[]? Image { get; }

    /// <summary>Why there is nothing to show here, or null.</summary>
    public string? Message { get; }

    public bool IsTree => Tree is not null;

    public bool IsText => Text is not null;

    public bool IsImage => Image is not null;

    public bool IsMessage => Message is not null;

    public static ValuePreview ForText(string title, string text, bool truncated) => new(title, text, truncated);

    public static ValuePreview ForImage(string title, byte[] image) => new(title, image: image);

    public static ValuePreview ForMessage(string title, string message) => new(title, message: message);

    /// <summary>A tree over a range of an origin's bytes, which stay the origin's.</summary>
    public static ValuePreview ForJson(string title, IByteOrigin origin, long offset, long length,
        IReadOnlyList<IValueHintProvider>? hintProviders = null)
        => ForSource(title, origin.OpenRange(offset, length), hintProviders, ownedOrigin: null);

    /// <summary>A tree over a document held in memory - a value decoded from another one.</summary>
    public static ValuePreview ForJson(string title, byte[] json, IReadOnlyList<IValueHintProvider>? hintProviders = null)
    {
        var origin = new MemoryByteOrigin(json, title);
        return ForSource(title, origin.Open(), hintProviders, origin);
    }

    private static ValuePreview ForSource(string title, IByteSource source, IReadOnlyList<IValueHintProvider>? hintProviders, IByteOrigin? ownedOrigin)
    {
        var session = IndexedSourceSession<JsonSparseIndex>.Start(source, JsonSparseIndex.StartIndexing);
        var reader = new JsonTreeReader(session.Bytes);
        var text = new JsonTreeText(session.Bytes, session.Index.Structure, reader);
        var bytes = session.Bytes;
        var tree = new TreeDocument(session.Index.Structure, reader, new JsonTreePainter(text, hintProviders, offerArrayTable: false),
            new TreeExpandState(TreeExpandDepth), () => bytes.AvailableLength);
        _ = TellTreeWhenIndexedAsync(session, tree);
        return new ValuePreview(title, tree: tree, session: session, ownedOrigin: ownedOrigin);
    }

    /// <summary>The tree draws from the bytes at once; once its index is done, jumps inside it
    /// are fast too, and the surface is told so it can size its scroll range.</summary>
    private static async Task TellTreeWhenIndexedAsync(IndexedSourceSession<JsonSparseIndex> session, TreeDocument tree)
    {
        try
        {
            await session.IndexingTask;
        }
        catch
        {
            // A malformed value still shows what can be read.
        }

        tree.NotifyGrew();
    }

    public void Dispose()
    {
        // Every surface lets go of the tree before the bytes it reads are released.
        Tree?.Close();
        session?.Dispose();
        ownedOrigin?.Dispose();
    }
}
