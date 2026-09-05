using System.Threading.Tasks;

namespace Argonaut.Shell;

/// <summary>
/// A document that can reveal a raw byte offset: implemented by
/// <see cref="Argonaut.Features.Raw.RawViewModel"/>, which is the one view where a byte offset
/// is a location the user can actually be taken to.
///
/// Same opt-in capability rule as <see cref="IPathNavigable"/> - see that interface for why
/// this is not a member of <see cref="IDocumentViewModel"/>.
/// </summary>
public interface IByteOffsetNavigable
{
    /// <summary>Resolves <paramref name="byteOffset"/> to a display row - waiting out an
    /// in-progress scan if necessary - and reveals it. A document torn down mid-resolve simply
    /// reveals nothing.</summary>
    Task JumpToByteOffsetAsync(long byteOffset);
}
