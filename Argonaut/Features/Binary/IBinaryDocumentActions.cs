using System.Threading.Tasks;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Binary;

/// <summary>
/// What the binary view offers for bytes it cannot draw: a copy saved where the user chooses, and
/// the document opened in the app the system uses for its kind. The shell implements it - asking
/// where to save and reaching another app are its business - and reports failures itself, so the
/// view only asks.
/// </summary>
public interface IBinaryDocumentActions
{
    Task SaveCopyAsync(IByteOrigin origin);

    Task OpenInDefaultAppAsync(IByteOrigin origin);
}
