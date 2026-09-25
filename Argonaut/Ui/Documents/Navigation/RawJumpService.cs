using System;
using Argonaut.Engine.Bytes;
using Argonaut.Ui.Notifications;

namespace Argonaut.Ui.Documents.Navigation;

/// <summary>
/// App-wide "show these bytes in the raw viewer" requests. Any view (e.g. JsonView's
/// truncated-value link, or its "show in text" button for the selected node) calls Request(...)
/// without needing a reference back to the shell; MainWindow is the sole subscriber and owns
/// switching views / driving the raw document there. Mirrors <see cref="ToastService"/>'s pattern.
/// </summary>
public static class RawJumpService
{
    public static event Action<ByteRange>? Requested;

    public static void Request(ByteRange range) => Requested?.Invoke(range);
}
