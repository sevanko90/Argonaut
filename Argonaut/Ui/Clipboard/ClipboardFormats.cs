namespace Argonaut.Ui.Clipboard;

/// <summary>
/// The platform clipboard formats that hand over bytes directly, by the name each platform knows
/// them by: macOS a Uniform Type Identifier, X11 and Wayland a mime type. Asking for bytes skips
/// the UTF-16 string a text read would build and transcode.
/// </summary>
public static class ClipboardFormats
{
    /// <summary>Text as UTF-8. Windows has no standard UTF-8 format, so it falls through to the
    /// string path there - which is why readers prefer these rather than require them.</summary>
    public static readonly string[] Utf8Text =
    {
        "public.utf8-plain-text",        // macOS
        "text/plain;charset=utf-8",      // X11 / Wayland
    };

    /// <summary>Bytes that are not text. macOS has a type for any data; elsewhere the octet-stream
    /// mime type is the convention, registered by that name on Windows.</summary>
    public static readonly string[] Binary =
    {
        "public.data",                   // macOS
        "application/octet-stream",      // X11 / Wayland / Windows
    };
}
