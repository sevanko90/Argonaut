using Argonaut.Engine.Detection;

namespace Argonaut.Ui.Documents;

/// <summary>
/// A document that writes line breaks - one that can be edited - and so follows its input's line
/// endings and can convert them. Opt-in, like <see cref="ISaveableDocument"/>: the shell counts
/// an input's line endings once (it owns the input) and hands the count down, and a document that
/// writes breaks reports back the conversion its next save will make. Members are observable
/// through the document's own property notifications.
/// </summary>
public interface ILineEndingDocument
{
    /// <summary>
    /// What the shell counted in this document's input. New lines and pasted text follow it - the
    /// prevailing kind of break, or the platform's own when there is nothing to follow.
    /// </summary>
    void UseInputLineEndings(LineEndingTally input);

    /// <summary>The break a new line inserts: the input's convention, a pending conversion's, or
    /// the platform's when the input has no breaks to follow - what the indicator shows for a
    /// document with none yet.</summary>
    LineEndingStyle LineEnding { get; }

    /// <summary>Whether <see cref="ConvertLineEndingsOnSave"/> can be asked for now: the document
    /// can be edited and saved, and no save is running.</summary>
    bool CanConvertLineEndings { get; }

    /// <summary>The kind of break the next save writes every line break as, or null when the save
    /// writes the document's breaks as they are.</summary>
    LineEndingStyle? PendingLineEnding { get; }

    /// <summary>
    /// Has the next save write every break as <paramref name="target"/>. Converted on the way into
    /// the file rather than in memory, so a multi-GB document costs no more than a save. Null, or
    /// the kind of break the input already uses throughout, cancels a pending conversion.
    /// </summary>
    void ConvertLineEndingsOnSave(LineEndingStyle? target);
}
