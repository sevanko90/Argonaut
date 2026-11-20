using System;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// What C leaves to the platform, and a layout needs settled: how long a <c>long</c> and a
/// <c>wchar_t</c> are, how wide a pointer is, and whether plain <c>char</c> is signed. Shown
/// beside the struct, because a wrong target is the usual reason a layout comes out wrong.
/// </summary>
public sealed record StructTarget(string Name, int LongSize, int WideCharSize, int PointerSize, bool CharIsSigned)
{
    /// <summary>64-bit macOS and Linux: <c>long</c> 8 bytes, <c>wchar_t</c> 4.</summary>
    public static StructTarget Lp64 { get; } = new("LP64 (macOS, Linux)", LongSize: 8, WideCharSize: 4, PointerSize: 8, CharIsSigned: true);

    /// <summary>64-bit Windows: <c>long</c> 4 bytes, <c>wchar_t</c> 2.</summary>
    public static StructTarget Llp64 { get; } = new("LLP64 (Windows)", LongSize: 4, WideCharSize: 2, PointerSize: 8, CharIsSigned: true);

    /// <summary>The target of the machine running the app - what a header compiled here meant.</summary>
    public static StructTarget ForThisMachine => OperatingSystem.IsWindows() ? Llp64 : Lp64;
}
