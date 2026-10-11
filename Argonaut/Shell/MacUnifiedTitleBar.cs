using System;
using System.Collections.Generic;
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
/// height and moves the three buttons to its vertical centre. AppKit lays them out again
/// whenever it sees fit - a resize, a title change, key status, and passes nothing in the app
/// can see coming - so <see cref="Watch"/> reports every change to the container's or the close
/// button's frame, and the window puts them back when they have moved.
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

        // The app paints the strip; AppKit's own title bar must paint nothing. A zoom by
        // double-clicking the title bar can un-hide its background view, a grey block over the
        // traffic lights' corner, so transparency is asserted again on every pass and that view
        // is hidden.
        ObjC.Send(nsWindow, ObjC.Selector("setTitlebarAppearsTransparent:"), 1);
        HideBackgroundViews(ObjC.Send(close, ObjC.Selector("superview")));

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

    /// <summary>Whether the container and the buttons are where <see cref="Apply"/> puts them.
    /// True off macOS, where there is nothing to place.</summary>
    public static bool IsInPlace(Window window, double titleBarHeight)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { Handle: var nsWindow } || nsWindow == 0)
            return true;

        var close = ObjC.Send(nsWindow, ObjC.Selector("standardWindowButton:"), 0);
        if (close == 0)
            return true;

        var container = ObjC.Send(ObjC.Send(close, ObjC.Selector("superview")), ObjC.Selector("superview"));
        var buttonFrame = ObjC.SendRect(close, ObjC.Selector("frame"));
        return container != 0
               && Math.Abs(ObjC.SendRect(container, ObjC.Selector("frame")).Height - titleBarHeight) < 0.5
               && Math.Abs(buttonFrame.Y - Math.Round((titleBarHeight - buttonFrame.Height) / 2)) < 0.5
               && Math.Abs(buttonFrame.X - FirstButtonX) < 0.5;
    }

    /// <summary>
    /// Calls <paramref name="moved"/> whenever AppKit changes the frame of the title bar
    /// container or the close button - including the changes <see cref="Apply"/> makes itself,
    /// so the callback checks <see cref="IsInPlace"/> rather than applying unconditionally. It
    /// runs inside AppKit's layout, so it should defer its work. Null off macOS; dispose to stop.
    /// </summary>
    public static IDisposable? Watch(Window window, Action moved)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { Handle: var nsWindow } || nsWindow == 0)
            return null;

        var close = ObjC.Send(nsWindow, ObjC.Selector("standardWindowButton:"), 0);
        var container = close == 0 ? 0 : ObjC.Send(ObjC.Send(close, ObjC.Selector("superview")), ObjC.Selector("superview"));
        if (container == 0)
            return null;

        return new FrameWatch(moved, container, close);
    }

    /// <summary>
    /// An Objective-C object registered for <c>NSViewFrameDidChangeNotification</c> on the watched
    /// views. AppKit can only notify an Objective-C object, so a class is defined at run time
    /// with one method, which finds its callback by the object's own handle.
    /// </summary>
    private sealed unsafe class FrameWatch : IDisposable
    {
        private const string ClassName = "ArgonautTitleBarFrameWatch";
        private static readonly Dictionary<nint, Action> Callbacks = new();
        private static nint watchClass;

        private nint observer;

        public FrameWatch(Action moved, params nint[] views)
        {
            observer = ObjC.Send(ObjC.Send(WatchClass(), ObjC.Selector("alloc")), ObjC.Selector("init"));
            Callbacks[observer] = moved;

            var center = ObjC.Send(ObjC.Class("NSNotificationCenter"), ObjC.Selector("defaultCenter"));
            var name = ObjC.String("NSViewFrameDidChangeNotification");
            foreach (var view in views)
            {
                ObjC.Send(view, ObjC.Selector("setPostsFrameChangedNotifications:"), 1);
                ObjC.AddObserver(center, ObjC.Selector("addObserver:selector:name:object:"), observer, ObjC.Selector("frameChanged:"), name, view);
            }
        }

        public void Dispose()
        {
            if (observer == 0)
                return;

            var center = ObjC.Send(ObjC.Class("NSNotificationCenter"), ObjC.Selector("defaultCenter"));
            ObjC.Send(center, ObjC.Selector("removeObserver:"), observer);
            Callbacks.Remove(observer);
            ObjC.Send(observer, ObjC.Selector("release"));
            observer = 0;
        }

        private static nint WatchClass()
        {
            if (watchClass != 0)
                return watchClass;

            watchClass = ObjC.Class(ClassName);
            if (watchClass != 0)
                return watchClass;

            watchClass = ObjC.AllocateClassPair(ObjC.Class("NSObject"), ClassName, 0);
            delegate* unmanaged<nint, nint, nint, void> frameChanged = &FrameChanged;
            ObjC.AddMethod(watchClass, ObjC.Selector("frameChanged:"), (nint)frameChanged, "v@:@");
            ObjC.RegisterClassPair(watchClass);
            return watchClass;
        }

        [UnmanagedCallersOnly]
        private static void FrameChanged(nint self, nint selector, nint notification)
        {
            if (Callbacks.TryGetValue(self, out var moved))
                moved();
        }
    }

    /// <summary>Hides the views AppKit paints a title bar's background with -
    /// <c>NSTitlebarBackgroundView</c>, and any material view - directly inside
    /// <paramref name="view"/>.</summary>
    private static void HideBackgroundViews(nint view)
    {
        if (view == 0)
            return;

        var subviews = ObjC.Send(view, ObjC.Selector("subviews"));
        long count = ObjC.Send(subviews, ObjC.Selector("count"));
        for (long i = 0; i < count; i++)
        {
            var subview = ObjC.Send(subviews, ObjC.Selector("objectAtIndex:"), (nint)i);
            if (Marshal.PtrToStringUTF8(ObjC.ClassName(subview)) is { } name && (name.Contains("TitlebarBackground", StringComparison.Ordinal) || name.Contains("VisualEffect", StringComparison.Ordinal)))
                ObjC.Send(subview, ObjC.Selector("setHidden:"), 1);
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

        [DllImport(Runtime, EntryPoint = "object_getClassName")]
        public static extern nint ClassName(nint obj);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern void AddObserver(nint receiver, nint selector, nint observer, nint observed, nint name, nint sender);

        [DllImport(Runtime)]
        private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Runtime, EntryPoint = "objc_allocateClassPair")]
        public static extern nint AllocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

        [DllImport(Runtime, EntryPoint = "objc_registerClassPair")]
        public static extern void RegisterClassPair(nint cls);

        [DllImport(Runtime, EntryPoint = "class_addMethod")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AddMethod(nint cls, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

        public static nint Class(string name) => objc_getClass(name);

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

        public static nint Selector(string name) => sel_registerName(name);
    }
}
