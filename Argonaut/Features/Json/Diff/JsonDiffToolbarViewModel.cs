using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// The diff document's header toolbar, reached through the existing
/// <see cref="Argonaut.Shell.IDocumentViewModel.Toolbar"/> seam (one type-keyed
/// DataTemplate in MainWindow.axaml, no other shell change): the two file names and the
/// "changes only" filter. The filter callback is injected rather than the row collection
/// itself, keeping this view model constructible before the rows exist.
/// </summary>
public sealed class JsonDiffToolbarViewModel : ObservableObject
{
    private readonly Action<bool> setChangesOnly;
    private bool changesOnly;

    public JsonDiffToolbarViewModel(string leftFileName, string rightFileName, Action<bool> setChangesOnly)
    {
        LeftFileName = leftFileName;
        RightFileName = rightFileName;
        this.setChangesOnly = setChangesOnly;
    }

    public string LeftFileName { get; }

    public string RightFileName { get; }

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
