using System;

namespace Argonaut.Infrastructure;

/// <summary>
/// One "view this array as a table" request, fully resolved by the JSON document that raised it.
/// </summary>
/// <param name="Path">File the array lives in.</param>
/// <param name="Offset">Byte offset of the array's opening bracket, relative to the FILE - the
/// raiser is responsible for converting out of its own mapping's coordinates first (see
/// JsonTokenInfo.Offset).</param>
/// <param name="Length">Byte length of the array, opening bracket through closing bracket
/// inclusive, so [Offset, Offset + Length) is a valid JSON document on its own.</param>
/// <param name="ArrayPath">JSONPath the array sits at in the source document - the banner's
/// text, and where Back navigates to.</param>
public readonly record struct ArrayTableRequest(IByteOrigin Origin, long Offset, long Length, string ArrayPath);

/// <summary>
/// App-wide "open this array as a table" requests. The JSON view model resolves the byte range
/// and raises one without needing a reference back to the shell; MainWindow is the sole
/// subscriber and owns publishing the table document. Mirrors <see cref="RawJumpService"/> and
/// <see cref="ToastService"/>.
/// </summary>
public static class ArrayTableService
{
    public static event Action<ArrayTableRequest>? Requested;

    public static void Request(ArrayTableRequest request) => Requested?.Invoke(request);
}
