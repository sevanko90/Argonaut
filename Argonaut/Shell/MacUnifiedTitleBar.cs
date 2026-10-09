using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Argonaut.Shell;

/// <summary>
/// Centres macOS's traffic lights in the app's own title bar.
///
/// The app draws its title bar (the client area extends under the window controls), and
/// Avalonia leaves the traffic lights where a plain 28pt title bar puts them - high in a taller
/// strip. AppKit offers no setting for that, so this does what Electron's
/// <c>trafficLightPosition</c> does: it grows the native title bar container to the strip's
/// height and moves the three buttons to its vertical centre. AppKit lays them out again on
/// resize and on entering or leaving full screen, so the window calls <see cref="Apply"/> again
/// whenever its size changes.
///
/// A no-op anywhere but macOS, and when the window has no native handle.
/// </summary>
internal static class MacUnifiedTitleBar
{
    /// <summary>Where the close button's left edge sits, as in a native unified toolbar.</summary>
    private const double FirstButtonX = 20;

    /// <summary>Left edge to left edge, between adjacent buttons.</summary>
    private const double ButtonPitch = 20;

    /// <summary>
    /// How wide the native container is left: just the traffic lights' corner. Full width, it
    /// lies over the app's own title-bar controls and takes their text input - a click focuses the
    /// search field but nothing typed reaches it. The app's strip handles dragging itself, so
    /// the container has nothing to do beyond holding the three buttons.
    /// </summary>
    private const double ContainerWidth = 80;

    public static void Apply(Window window, double titleBarHeight)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { Handle: var nsWindow } || nsWindow == 0)
            return;

        var close = ObjC.Send(nsWindow, ObjC.Selector("standardWindowButton:"), 0);
        if (close == 0)
            return;

        // Button -> NSTitlebarView -> NSTitlebarContainerView, which AppKit pins to the top of
        // the window at the standard height.
        var container = ObjC.Send(ObjC.Send(close, ObjC.Selector("superview")), ObjC.Selector("superview"));
        if (container == 0)
            return;

        var windowFrame = ObjC.SendRect(nsWindow, ObjC.Selector("frame"));
        var containerFrame = ObjC.SendRect(container, ObjC.Selector("frame"));
        containerFrame.Height = titleBarHeight;
        containerFrame.Y = windowFrame.Height - titleBarHeight;
        containerFrame.X = 0;
        containerFrame.Width = ContainerWidth;
        ObjC.Send(container, ObjC.Selector("setFrame:"), containerFrame);

        var buttonHeight = ObjC.SendRect(close, ObjC.Selector("frame")).Height;
        double y = Math.Round((titleBarHeight - buttonHeight) / 2);
        for (int kind = 0; kind < 3; kind++) // close, miniaturize, zoom
        {
            var button = ObjC.Send(nsWindow, ObjC.Selector("standardWindowButton:"), kind);
            if (button != 0)
                ObjC.Send(button, ObjC.Selector("setFrameOrigin:"), new NSPoint(FirstButtonX + kind * ButtonPitch, y));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NSPoint(double X, double Y);

    /// <summary>The Objective-C runtime calls the title bar needs. <c>objc_msgSend</c> has no
    /// fixed signature, so each shape used is declared against it. A rect is returned in
    /// registers on arm64 and through <c>objc_msgSend_stret</c> on x64.</summary>
    private static class ObjC
    {
        private const string Runtime = "/usr/lib/libobjc.A.dylib";

        [DllImport(Runtime)]
        private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint Send(nint receiver, nint selector, nint argument);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern void Send(nint receiver, nint selector, NSRect argument);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern void Send(nint receiver, nint selector, NSPoint argument);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        private static extern NSRect SendRectArm64(nint receiver, nint selector);

        [DllImport(Runtime, EntryPoint = "objc_msgSend_stret")]
        private static extern void SendRectX64(out NSRect result, nint receiver, nint selector);

        public static nint Send(nint receiver, nint selector) => Send(receiver, selector, 0);

        public static NSRect SendRect(nint receiver, nint selector)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return SendRectArm64(receiver, selector);

            SendRectX64(out var result, receiver, selector);
            return result;
        }

        public static nint Selector(string name) => sel_registerName(name);
    }
}
