using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argonaut.Ui.Launching;

namespace Argonaut.Shell.Launching;

/// <summary>
/// Opens folders and addresses through <c>NSWorkspace.openURL:</c>, the system's own route to a
/// URL's handler - Finder for a folder, the default browser or mail client for an address. It
/// starts no process, which is what makes it the one route the Mac App Store sandbox allows, and
/// every macOS build uses it so the ordinary build behaves exactly as the store build does.
///
/// If the Objective-C runtime cannot be reached the request goes to <paramref name="fallback"/>,
/// the way <c>MacFilePicker</c> falls back. The store build passes none, since shelling out is
/// what its sandbox forbids.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSystemLauncher : ISystemLauncher
{
    private readonly ISystemLauncher? fallback;

    public MacSystemLauncher(ISystemLauncher? fallback) => this.fallback = fallback;

    public void ShowFolder(string path)
    {
        if (!TryOpen("fileURLWithPath:", path))
            fallback?.ShowFolder(path);
    }

    public void OpenAddress(Uri address)
    {
        if (!TryOpen("URLWithString:", address.AbsoluteUri))
            fallback?.OpenAddress(address);
    }

    /// <returns>False only if the runtime could not be reached. A URL that does not parse, or
    /// that nothing on the system handles, is a failure to open, not a reason to fall back:
    /// the fallback would fail the same way.</returns>
    private static bool TryOpen(string urlFactory, string target)
    {
        try
        {
            var url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Selector(urlFactory), ObjC.String(target));
            if (url != 0)
            {
                var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Selector("sharedWorkspace"));
                ObjC.Send(workspace, ObjC.Selector("openURL:"), url);
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The few Objective-C runtime calls opening a URL needs. <c>objc_msgSend</c> has
    /// no fixed signature, so each shape used is declared against it.</summary>
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
