namespace Argonaut.Shell.Dialogs;

/// <summary>What to do with a request that has changes nobody saved, as the pane closes.</summary>
public enum RequestSaveChoice
{
    Cancel,

    /// <summary>Close without saving; a saved request it came from stays as it was.</summary>
    Discard,

    /// <summary>Overwrite the saved request the pane was opened from.</summary>
    Update,

    /// <summary>Save it as its own entry - for a request never saved, simply "Save".</summary>
    SaveAsNew,
}

/// <summary>The question asked as a request pane with unsaved changes closes.</summary>
/// <param name="RequestName">The saved request's name, or the request's own for one never saved.</param>
/// <param name="IsSaved">Whether the pane came from a saved request, so Update is offered beside
/// Save as new.</param>
public sealed record RequestSaveQuestion(string RequestName, bool IsSaved);
