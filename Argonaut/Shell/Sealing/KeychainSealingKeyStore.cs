using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell.Sealing;

/// <summary>
/// The sealing key as a generic-password item in the user's login Keychain - outside the app's
/// own folders, so it survives a reinstall, and the same item in the GitHub and App Store builds.
///
/// Called through Security.framework and CoreFoundation directly: a query is a CFDictionary of
/// the framework's own key constants, read out of the framework rather than spelled as strings.
/// Anything unavailable (the frameworks not loading, a locked keychain, access refused) reads as
/// no key, or a key not kept - never an exception - and sealing then degrades as the sealer
/// documents.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainSealingKeyStore(string service, string account) : ISealingKeyStore
{
    public const string DefaultService = "Argonaut";
    public const string DefaultAccount = "settings-sealing-key";

    public byte[]? LoadKey()
    {
        try
        {
            using var query = Native.Query(service, account, returnData: true);
            if (Native.SecItemCopyMatching(query.Handle, out nint result) != Native.Success || result == 0)
                return null;

            try
            {
                return Native.ReadData(result);
            }
            finally
            {
                Native.CFRelease(result);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool SaveKey(byte[] key)
    {
        try
        {
            using var item = Native.Query(service, account, returnData: false, value: key);
            return Native.SecItemAdd(item.Handle, 0) == Native.Success;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Removes the item - tests clean up with it; the app never deletes its key.</summary>
    internal void Delete()
    {
        using var query = Native.Query(service, account, returnData: false);
        Native.SecItemDelete(query.Handle);
    }

    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8Encoding = 0x08000100;

        public const int Success = 0;

        [DllImport(Security)]
        public static extern int SecItemCopyMatching(nint query, out nint result);

        [DllImport(Security)]
        public static extern int SecItemAdd(nint attributes, nint result);

        [DllImport(Security)]
        public static extern int SecItemDelete(nint query);

        [DllImport(CoreFoundation)]
        private static extern nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallBacks, nint valueCallBacks);

        [DllImport(CoreFoundation)]
        private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);

        [DllImport(CoreFoundation)]
        private static extern nint CFDataGetLength(nint data);

        [DllImport(CoreFoundation)]
        private static extern nint CFDataGetBytePtr(nint data);

        [DllImport(CoreFoundation)]
        private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(nint value);

        private static readonly Lazy<(nint Security, nint CoreFoundation)> Libraries =
            new(() => (NativeLibrary.Load(Security), NativeLibrary.Load(CoreFoundation)));

        /// <summary>The value of an exported CFTypeRef constant such as <c>kSecClass</c>.</summary>
        private static nint Constant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

        /// <summary>The address of an exported struct such as the dictionary callbacks.</summary>
        private static nint Address(nint library, string name) => NativeLibrary.GetExport(library, name);

        /// <summary>The generic-password query for one service and account; with
        /// <paramref name="returnData"/>, asking for the one item's data back, and with
        /// <paramref name="value"/>, the attributes of an item to add.</summary>
        public static OwnedCFDictionary Query(string service, string account, bool returnData, byte[]? value = null)
        {
            var (security, coreFoundation) = Libraries.Value;
            var owned = new OwnedCFDictionary();

            nint serviceText = owned.Own(CFStringCreateWithCString(0, service, Utf8Encoding));
            nint accountText = owned.Own(CFStringCreateWithCString(0, account, Utf8Encoding));

            var keys = new System.Collections.Generic.List<nint>
            {
                Constant(security, "kSecClass"),
                Constant(security, "kSecAttrService"),
                Constant(security, "kSecAttrAccount"),
            };
            var values = new System.Collections.Generic.List<nint>
            {
                Constant(security, "kSecClassGenericPassword"),
                serviceText,
                accountText,
            };

            if (returnData)
            {
                keys.Add(Constant(security, "kSecReturnData"));
                values.Add(Constant(coreFoundation, "kCFBooleanTrue"));
                keys.Add(Constant(security, "kSecMatchLimit"));
                values.Add(Constant(security, "kSecMatchLimitOne"));
            }

            if (value is not null)
            {
                keys.Add(Constant(security, "kSecValueData"));
                values.Add(owned.Own(CFDataCreate(0, value, value.Length)));
            }

            owned.Handle = CFDictionaryCreate(0, keys.ToArray(), values.ToArray(), keys.Count,
                Address(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                Address(coreFoundation, "kCFTypeDictionaryValueCallBacks"));
            return owned;
        }

        public static byte[] ReadData(nint data)
        {
            var bytes = new byte[(int)CFDataGetLength(data)];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>A CFDictionary and the CF values made to build it, released together. The
        /// dictionary retains what it holds, so releasing both is the balanced count.</summary>
        public sealed class OwnedCFDictionary : IDisposable
        {
            private readonly System.Collections.Generic.List<nint> parts = [];

            public nint Handle { get; set; }

            public nint Own(nint value)
            {
                if (value != 0)
                    parts.Add(value);
                return value;
            }

            public void Dispose()
            {
                if (Handle != 0)
                    CFRelease(Handle);
                foreach (var part in parts)
                    CFRelease(part);
            }
        }
    }
}
