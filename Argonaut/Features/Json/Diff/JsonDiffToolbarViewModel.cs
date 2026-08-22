using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// The diff document's header toolbar, reached through the existing
/// <see cref="Argonaut.Shell.IDocumentViewModel.Toolbar"/> seam (one type-keyed
/// DataTemplate in MainWindow.axaml, no other shell change): the "changes only" filter and
/// the diff-stepping buttons. The file names live in the window title instead - one place,
/// where they identify the document rather than competing with what the toolbar is for. The
/// filter callback is injected rather than the row collection itself, keeping this view model
/// constructible before the rows exist.
/// </summary>
public sealed class JsonDiffToolbarViewModel : ObservableObject
{
    private readonly Action<bool> setChangesOnly;
    private readonly Action goToPreviousDiff;
    private readonly Action goToNextDiff;
    private bool changesOnly;

    public JsonDiffToolbarViewModel(Action<bool> setChangesOnly,
        Action goToPreviousDiff, Action goToNextDiff)
    {
        this.setChangesOnly = setChangesOnly;
        this.goToPreviousDiff = goToPreviousDiff;
        this.goToNextDiff = goToNextDiff;
    }

    public void GoToPreviousDiff() => goToPreviousDiff();

    public void GoToNextDiff() => goToNextDiff();

    public bool ChangesOnly
    {
        get => changesOnly;
        set
        {
            if (!SetField(ref changesOnly, value))
                return;

            setChangesOnly(value);
        }
    }
}
