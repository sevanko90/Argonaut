using System.Threading.Tasks;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Hex;

/// <summary>
/// What the hex view's toolbar offers for a document's bytes: a copy saved where the user chooses,
/// and the document opened in the app the system uses for its kind. The shell implements it - asking
/// where to save and reaching another app are its business - and reports failures itself, so the
/// toolbar only asks.
/// </summary>
public interface IDocumentExportActions
{
    Task SaveCopyAsync(IByteOrigin origin);

    Task OpenInDefaultAppAsync(IByteOrigin origin);
}
