using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Selection;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Ui.Documents.Numbering;

/// <summary>
/// The one switch between an excerpt's source line numbers and counting from its start, shared
/// by every view that shows a line number or an offset and by the control that flips it. Views
/// follow it (<see cref="IndexedDocumentViewModel.FollowLineNumbering"/>) rather than each
/// holding a copy, so flipping it renumbers whatever is open at once.
///
/// UI-thread only, like the settings block behind it.
/// </summary>
public sealed class LineNumberingPreference : ObservableObject
{
    private readonly LineNumberingSettings settings;

    public LineNumberingPreference(LineNumberingSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>See <see cref="LineNumberingSettings.NumberExcerptsFromSource"/>.</summary>
    public bool NumberExcerptsFromSource
    {
        get => this.settings.NumberExcerptsFromSource;
        set
        {
            if (this.settings.NumberExcerptsFromSource == value)
                return;

            this.settings.NumberExcerptsFromSource = value;
            OnPropertyChanged();
        }
    }

    /// <summary>How <paramref name="origin"/>'s lines are numbered now: by the file for an
    /// excerpt or a selection when that is the choice, from the start otherwise. One taken by
    /// bytes does not know its line numbers, so by the file it labels lines by where they start.</summary>
    public LineNumbering For(IByteOrigin? origin)
    {
        if (!NumberExcerptsFromSource)
            return LineNumbering.FromStart;

        return origin switch
        {
            ExcerptByteOrigin { Lines: { } lines } excerpt => LineNumbering.Starting(lines.FirstLine, excerpt.Range.Offset),
            ExcerptByteOrigin excerpt => LineNumbering.ByLineStarts(excerpt.Range.Offset, excerpt.Range.Length),
            SelectionByteOrigin selection => selection.Numbering,
            _ => LineNumbering.FromStart,
        };
    }
}
