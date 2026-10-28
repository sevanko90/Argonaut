using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>
/// What <see cref="TreeDocumentViewModel"/> needs of a format's background index: the structure
/// the tree is walked through, and the scan state a reveal waits on. Declared here because the
/// tree view model consumes it; each format's index implements it from above.
/// </summary>
public interface ITreeIndex : IBackgroundIndex
{
    /// <summary>The containers recorded so far; grows underneath the tree while the scan runs.</summary>
    SparseContainerIndex Structure { get; }
}
