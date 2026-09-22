using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Shell;

/// <summary>What a save came to.</summary>
public enum DocumentSaveOutcome
{
    /// <summary>The destination holds the document, and the document now reads from it.</summary>
    Saved,

    /// <summary>Nothing on disk changed, and the document - edits included - is as it was.</summary>
    NotSaved,

    /// <summary>
    /// The swap failed and the original could not be reopened afterwards, so the document can no
    /// longer be shown. What was being saved is kept on disk, and the message says where.
    /// </summary>
    NotSavedAndDocumentLost,
}

/// <summary>A save's outcome and, unless it succeeded, what to tell the user.</summary>
public readonly record struct DocumentSaveResult(DocumentSaveOutcome Outcome, string? Message)
{
    public static DocumentSaveResult Saved { get; } = new(DocumentSaveOutcome.Saved, null);

    public static DocumentSaveResult NotSaved(string message) => new(DocumentSaveOutcome.NotSaved, message);
}

/// <summary>
/// A document that can write itself to a file: implemented by
/// <see cref="Argonaut.Features.Raw.RawViewModel"/>, the one view that edits. Same opt-in
/// capability rule as <see cref="IPathNavigable"/> - see that interface for why this is not a
/// member of <see cref="IDocumentViewModel"/>.
///
/// The document owns the whole save, because only it knows what it reads through and so what has
/// to be let go of before the swap (see docs/save-plan.md). The shell owns the parts either side:
/// choosing the destination, stopping anything else reading the file, and adopting the new origin.
/// </summary>
public interface ISaveableDocument
{
    /// <summary>True when the document reads differently from its file. Observable through the
    /// document's own property notifications.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>Whether a save can start now - false while the document is still loading, and
    /// while a save is already running. Observable.</summary>
    bool CanSave { get; }

    /// <summary>
    /// Writes the document to <paramref name="destination"/> through <paramref name="replacer"/>
    /// and, when that succeeds, reopens the document over it - so afterwards it reads from the
    /// file it was saved to, with no unsaved changes.
    ///
    /// The caller must have stopped and joined every other reader of the document's current file
    /// first (a running search), because the swap needs that file unmapped. UI thread only.
    /// </summary>
    Task<DocumentSaveResult> SaveAsync(IByteOrigin destination, IFileReplacer replacer, IProgressReporter? progress);
}
