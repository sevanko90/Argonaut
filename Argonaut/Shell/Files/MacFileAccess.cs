using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argonaut.Engine.Bytes;

namespace Argonaut.Shell.Files;

/// <summary>
/// <see cref="IFileAccess"/> through <c>NSURL</c> bookmarks, in every macOS build. A bookmark is
/// the system's own way back to a file: it follows the file when it is moved or renamed, and - as
/// a security-scoped bookmark - it is what lets a sandboxed build open a remembered file again
/// after a relaunch. The GitHub build uses it too, so both builds reopen files the same way.
///
/// A security-scoped bookmark is asked for first and a plain one accepted when that is refused
/// (an unsandboxed build may not be given one). Resolving never prompts and never mounts a volume,
/// so a file on a share that is not connected falls back to its saved path at once rather than
/// stalling the UI - and the caller then finds nothing there. Anything that goes wrong in the
/// runtime is the same fallback, never an exception.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacFileAccess : IFileAccess
{
    private const nuint CreationWithSecurityScope = 1 << 11;
    private const nuint ResolutionWithoutUI = 1 << 8;
    private const nuint ResolutionWithoutMounting = 1 << 9;
    private const nuint ResolutionWithSecurityScope = 1 << 10;

    public FileLocator Describe(string path)
    {
        var locator = new FileLocator(path);
        try
        {
            using var pool = new AutoreleasePool();
            nint url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Selector("fileURLWithPath:"), ObjC.String(locator.Path));
            return url == 0 ? locator : new FileLocator(locator.Path, BookmarkOf(url));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return locator;
        }
    }

    public FileAccessGrant Open(FileLocator locator)
    {
        if (locator.Bookmark is not { Length: > 0 } bookmark)
            return new FileAccessGrant(locator);

        try
        {
            using var pool = new AutoreleasePool();
            nint data = ObjC.Data(bookmark);
            nint url = Resolve(data, ResolutionWithSecurityScope, out bool stale);
            bool scoped = url != 0;
            if (url == 0)
                url = Resolve(data, 0, out stale);
            if (url == 0 || ObjC.PathOf(url) is not { } path)
                return new FileAccessGrant(locator);

            // Retained past the pool, since stopping access happens when the document closes.
            bool accessing = scoped && ObjC.SendBool(url, ObjC.Selector("startAccessingSecurityScopedResource"));
            if (accessing)
                ObjC.Send(url, ObjC.Selector("retain"));

            // Moved only when it is no longer where it was saved: a bookmark resolves symlinks, so a
            // file reached through one (/var is /private/var) comes back under another spelling of
            // the same place. A stale bookmark is refreshed either way, so the next open resolves
            // the file as it is now.
            bool moved = !System.IO.File.Exists(locator.Path);
            var found = moved || stale
                ? new FileLocator(moved ? path : locator.Path, BookmarkOf(url) ?? bookmark)
                : locator;

            return new FileAccessGrant(found, accessing ? () => StopAccessing(url) : null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new FileAccessGrant(locator);
        }
    }

    private static nint Resolve(nint data, nuint scope, out bool stale)
    {
        nint url = ObjC.SendResolve(ObjC.Class("NSURL"),
            ObjC.Selector("URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:"),
            data, scope | ResolutionWithoutUI | ResolutionWithoutMounting, 0, out byte isStale, out _);
        stale = isStale != 0;
        return url;
    }

    /// <summary>The bookmark for <paramref name="url"/>, security-scoped where allowed, or null
    /// when the system makes none (the file is gone, the volume will not have one).</summary>
    private static byte[]? BookmarkOf(nint url)
    {
        var selector = ObjC.Selector("bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:");
        nint data = ObjC.SendBookmark(url, selector, CreationWithSecurityScope, 0, 0, out _);
        if (data == 0)
            data = ObjC.SendBookmark(url, selector, 0, 0, 0, out _);

        return data == 0 ? null : ObjC.Bytes(data);
    }

    private static void StopAccessing(nint url)
    {
        ObjC.Send(url, ObjC.Selector("stopAccessingSecurityScopedResource"));
        ObjC.Send(url, ObjC.Selector("release"));
    }

    /// <summary>Drains what the runtime calls hand back autoreleased, on threads - the test host's
    /// among them - that have no run loop doing it.</summary>
    private readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint pool;

        public AutoreleasePool() => pool = ObjC.PushPool();

        public void Dispose() => ObjC.PopPool(pool);
    }

    /// <summary>The Objective-C runtime calls bookmarks need. <c>objc_msgSend</c> has no fixed
    /// signature, so each shape used is declared against it.</summary>
    private static class ObjC
    {
        private const string Runtime = "/usr/lib/libobjc.A.dylib";

        [DllImport(Runtime)]
        private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Runtime)]
        private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
        public static extern nint PushPool();

        [DllImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
        public static extern void PopPool(nint pool);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint Send(nint receiver, nint selector);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint Send(nint receiver, nint selector, nint argument);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        private static extern nint SendBytes(nint receiver, nint selector, byte[] bytes, nuint length);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern byte SendByte(nint receiver, nint selector);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint SendBookmark(nint receiver, nint selector, nuint options, nint keys, nint relativeTo, out nint error);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern nint SendResolve(nint receiver, nint selector, nint data, nuint options, nint relativeTo, out byte isStale, out nint error);

        public static nint Class(string name) => objc_getClass(name);

        public static nint Selector(string name) => sel_registerName(name);

        public static bool SendBool(nint receiver, nint selector) => SendByte(receiver, selector) != 0;

        public static nint String(string text) => SendUtf8(Class("NSString"), Selector("stringWithUTF8String:"), text);

        public static nint Data(byte[] bytes) => SendBytes(Class("NSData"), Selector("dataWithBytes:length:"), bytes, (nuint)bytes.Length);

        public static byte[] Bytes(nint data)
        {
            var bytes = new byte[(int)Send(data, Selector("length"))];
            Marshal.Copy(Send(data, Selector("bytes")), bytes, 0, bytes.Length);
            return bytes;
        }

        public static string? PathOf(nint url)
        {
            nint path = Send(url, Selector("path"));
            return path == 0 ? null : Marshal.PtrToStringUTF8(Send(path, Selector("UTF8String")));
        }
    }
}
