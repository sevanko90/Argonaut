using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// App-wide "jump to this byte offset in the raw viewer" requests. Any view (e.g. JsonView's
/// truncated-value link) calls Request(...) without needing a reference back to the shell;
/// MainWindow is the sole subscriber and owns switching views / driving RawViewModel there.
/// Mirrors <see cref="ToastService"/>'s pattern.
/// </summary>
public static class RawJumpService
{
    public static event Action<long>? Requested;

    public static void Request(long byteOffset) => Requested?.Invoke(byteOffset);
}
