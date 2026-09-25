using System.Threading.Tasks;
using Argonaut.Engine.Bytes;

namespace Argonaut.Ui.Documents.Navigation;

/// <summary>
/// A document whose position can be expressed as a byte range of the input, in both directions:
/// it can say which bytes the user is on, and it can be taken to a byte range. File offsets are
/// the one coordinate every view of the same input shares, so this is what lets a view switch
/// land on the same place - the JSON view's selected node becomes the text view's selection, and
/// the text view's caret becomes the JSON view's selected node.
///
/// Same opt-in capability rule as <see cref="IPathNavigable"/> - see that interface for why
/// this is not a member of <see cref="IDocumentViewModel"/>.
/// </summary>
public interface IByteRangeNavigable
{
    /// <summary>The bytes the user is on, relative to the start of the input - a selection, or a
    /// caret as a zero-length range. Null when there is nothing to carry over.</summary>
    ByteRange? SelectedByteRange { get; }

    /// <summary>Resolves <paramref name="range"/> to whatever this view shows for those bytes -
    /// waiting out an in-progress scan if necessary - and reveals it. A document torn down
    /// mid-resolve simply reveals nothing.</summary>
    Task RevealByteRangeAsync(ByteRange range);
}
