using Avalonia.Automation.Peers;

namespace Argonaut.Ui.Tree;

/// <summary>
/// What assistive technology sees of a <see cref="TreeSurface"/>: a tree, named by its selected
/// row's text when nothing else names it, so moving the selection with the keyboard is read out.
/// The surface draws its rows rather than hosting a control per row, so there are no child
/// elements for a reader to walk; the selection is the thing to announce.
/// </summary>
public sealed class TreeSurfaceAutomationPeer(TreeSurface owner) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;

    protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : owner.SelectedRowText;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
