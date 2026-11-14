using System;

namespace Argonaut.Ui.Notifications;

/// <summary>One thing a toast offers to do about what it reports - "Remove from list".</summary>
public sealed record ToastAction(string Label, Action Run);

/// <summary>
/// App-wide toast notifications. Any view (JsonView, NdJsonView, ...) calls Show(...) to
/// post a message without needing a reference back to the shell; MainWindow is the sole
/// subscriber and owns the actual toast UI.
/// </summary>
public static class ToastService
{
    public static event Action<string>? Requested;

    /// <summary>A toast with an action, which stays up long enough to be acted on.</summary>
    public static event Action<string, ToastAction>? ActionRequested;

    public static void Show(string message) => Requested?.Invoke(message);

    public static void Show(string message, ToastAction action) => ActionRequested?.Invoke(message, action);
}
