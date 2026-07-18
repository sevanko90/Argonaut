using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// App-wide toast notifications. Any view (JsonView, NdJsonView, ...) calls Show(...) to
/// post a message without needing a reference back to the shell; MainWindow is the sole
/// subscriber and owns the actual toast UI.
/// </summary>
public static class ToastService
{
    public static event Action<string>? Requested;

    public static void Show(string message) => Requested?.Invoke(message);
}
