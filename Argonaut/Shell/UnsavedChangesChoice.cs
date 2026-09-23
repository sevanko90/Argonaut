namespace Argonaut.Shell;

/// <summary>What the user chose when leaving a document with unsaved changes. <see cref="Cancel"/>
/// is the default, so a prompt closed any other way - the window's own close button - leaves the
/// document alone.</summary>
public enum UnsavedChangesChoice
{
    Cancel,
    Save,
    Discard,
}
