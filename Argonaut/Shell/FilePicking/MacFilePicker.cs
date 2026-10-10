using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Argonaut.Shell.FilePicking;

/// <summary>
/// The macOS open panel, able to show hidden files, which is where dotfile configuration lives.
/// <c>NSOpenPanel.showsHiddenFiles</c> is per panel, so the user's Finder setting is untouched,
/// and the panel is the same one Avalonia shows - it is the sandbox's own route to a user-chosen
/// file. Avalonia exposes no way to set the property, so the panel is driven through the
/// Objective-C runtime.
///
/// The panel runs modally on the UI thread, which is the main thread on macOS. If the runtime
/// cannot be reached the request goes to <paramref name="fallback"/>, so a failure here costs the
/// hidden files, not the ability to open a file.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacFilePicker : IFilePicker
{
    private const nint ModalResponseOk = 1;

    private readonly IFilePicker fallback;

    public MacFilePicker(IFilePicker fallback) => this.fallback = fallback;

    public Task<string?> PickFileAsync(string title, string? startFolder = null, bool showsHiddenFiles = true)
    {
        try
        {
            if (TryRunOpenPanel(title, startFolder, showsHiddenFiles, out var path))
                return Task.FromResult(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return fallback.PickFileAsync(title, startFolder, showsHiddenFiles);
    }

    /// <returns>False if the panel could not be created; otherwise true, with
    /// <paramref name="path"/> null when the user cancelled.</returns>
    private static bool TryRunOpenPanel(string title, string? startFolder, bool showsHiddenFiles, out string? path)
    {
        path = null;

        var panel = ObjC.Send(ObjC.Class("NSOpenPanel"), ObjC.Selector("openPanel"));
        if (panel == 0)
            return false;

        ObjC.Send(panel, ObjC.Selector("setCanChooseFiles:"), (byte)1);
        ObjC.Send(panel, ObjC.Selector("setCanChooseDirectories:"), (byte)0);
        ObjC.Send(panel, ObjC.Selector("setAllowsMultipleSelection:"), (byte)0);
        ObjC.Send(panel, ObjC.Selector("setShowsHiddenFiles:"), showsHiddenFiles ? (byte)1 : (byte)0);
        ObjC.Send(panel, ObjC.Selector("setTitle:"), ObjC.String(title));

        if (startFolder is not null)
        {
            var url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Selector("fileURLWithPath:"), ObjC.String(startFolder));
            ObjC.Send(panel, ObjC.Selector("setDirectoryURL:"), url);
        }

        if (ObjC.Send(panel, ObjC.Selector("runModal")) != ModalResponseOk)
            return true;

        var chosen = ObjC.Send(panel, ObjC.Selector("URL"));
        if (chosen == 0)
            return true;

        var chosenPath = ObjC.Send(chosen, ObjC.Selector("path"));
        path = chosenPath == 0
            ? null
            : Marshal.PtrToStringUTF8(ObjC.Send(chosenPath, ObjC.Selector("UTF8String")));
        return true;
    }

    /// <summary>The few Objective-C runtime calls the panel needs. <c>objc_msgSend</c> has no
    /// fixed signature, so each shape used is declared against it.</summary>
    private static class ObjC
    {
        private const string Runtime = "/usr/lib/libobjc.A.dylib";

        [DllImport(Runtime)]
        private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Runtime)]
        private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint Send(nint receiver, nint selector);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint Send(nint receiver, nint selector, nint argument);

        // An Objective-C BOOL: one byte.
        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint Send(nint receiver, nint selector, byte argument);

        public static nint Class(string name) => objc_getClass(name);

        public static nint Selector(string name) => sel_registerName(name);

        /// <summary>An autoreleased <c>NSString</c>, which the run loop's pool frees.</summary>
        public static nint String(string value)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }
    }
}
