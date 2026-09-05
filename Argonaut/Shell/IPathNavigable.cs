using System.Threading.Tasks;

namespace Argonaut.Shell;

/// <summary>
/// A document that can reveal a JSONPath: implemented by <see cref="Argonaut.Features.Json.JsonViewModel"/>
/// today, and by any future tree-shaped view without the shell changing.
///
/// An opt-in capability, deliberately NOT a member of <see cref="IDocumentViewModel"/> - that
/// interface is the surface every document genuinely shares, and "reveal a path" is meaningless
/// for a raw byte view or a CSV grid. The shell asks whether a document can do the thing, never
/// what the document is, so a reveal flow (Back out of an array table; jump to a failure
/// location) grows no <c>case JsonViewModel:</c> arm as views are added.
/// </summary>
public interface IPathNavigable
{
    /// <summary>Resolves <paramref name="path"/> against this document and selects/reveals the
    /// token it names, or surfaces its own error if it cannot. Safe to call on a
    /// still-indexing document - implementations wait for coverage.</summary>
    Task NavigateToPathAsync(string path);
}
