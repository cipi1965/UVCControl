using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UVCControl.Core.Platform.MacOS;

/// <summary>Minimal IOKit / CoreFoundation bindings for USB device access.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class IOKit
{
    private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int KIOReturnSuccess = 0;
    public const int KIOReturnNoDevice = unchecked((int)0xE00002C0);
    private const uint KCFStringEncodingUTF8 = 0x08000100;
    private const int KCFNumberSInt64Type = 4;

    // MARK: IOKit

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKitLib)]
    public static partial IntPtr IORegistryEntryIDMatching(ulong entryId);

    /// <remarks>Consumes one reference to <paramref name="matching"/>.</remarks>
    [LibraryImport(IOKitLib)]
    public static partial int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

    /// <remarks>Consumes one reference to <paramref name="matching"/>.</remarks>
    [LibraryImport(IOKitLib)]
    public static partial uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);

    [LibraryImport(IOKitLib)]
    public static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKitLib)]
    public static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int IORegistryEntryGetParentEntry(uint entry, string plane, out uint parent);

    [LibraryImport(IOKitLib)]
    public static partial int IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryId);

    [LibraryImport(IOKitLib)]
    public static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(IOKitLib)]
    public static partial int IOCreatePlugInInterfaceForService(uint service, IntPtr pluginType, IntPtr interfaceType,
                                                                out IntPtr theInterface, out int theScore);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial IntPtr mach_error_string(int error);

    // MARK: CoreFoundation

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundationLib)]
    private static partial nuint CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundationLib)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundationLib)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string str, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFStringGetLength(IntPtr str);

    [LibraryImport(CoreFoundationLib)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(IntPtr number, int type, out long value);

    [LibraryImport(CoreFoundationLib)]
    public static partial IntPtr CFUUIDGetConstantUUIDWithBytes(IntPtr allocator,
        byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7,
        byte b8, byte b9, byte b10, byte b11, byte b12, byte b13, byte b14, byte b15);

    // MARK: Helpers

    public static string ErrorString(int code) => unchecked((uint)code) switch
    {
        0xE000404F => "Stalled (not supported by device)",
        0xE00002D6 => "Timed out",
        0xE00002C0 => "No device",
        0xE00002C5 => "Exclusive access",
        _ => $"0x{code:X8} {Marshal.PtrToStringUTF8(mach_error_string(code))}",
    };

    public static ulong RegistryId(uint entry) =>
        IORegistryEntryGetRegistryEntryID(entry, out var id) == KIOReturnSuccess ? id : 0;

    private static IntPtr CopyProperty(uint entry, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, KCFStringEncodingUTF8);
        try
        {
            return IORegistryEntryCreateCFProperty(entry, cfKey, IntPtr.Zero, 0);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    public static string? GetString(uint entry, string key)
    {
        var value = CopyProperty(entry, key);
        if (value == IntPtr.Zero) return null;
        try
        {
            if (CFGetTypeID(value) != CFStringGetTypeID()) return null;
            var size = CFStringGetLength(value) * 4 + 1;
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (!CFStringGetCString(value, p, size, KCFStringEncodingUTF8)) return null;
            }
            var length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }
        finally
        {
            CFRelease(value);
        }
    }

    public static long? GetInt(uint entry, string key)
    {
        var value = CopyProperty(entry, key);
        if (value == IntPtr.Zero) return null;
        try
        {
            return CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, KCFNumberSInt64Type, out var result)
                ? result
                : null;
        }
        finally
        {
            CFRelease(value);
        }
    }
}

/// <summary>Mirrors IOUSBDevRequestTO from IOUSBLib.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IOUSBDevRequestTO
{
    public byte BmRequestType;
    public byte BRequest;
    public ushort WValue;
    public ushort WIndex;
    public ushort WLength;
    public IntPtr PData;
    public uint WLenDone;
    public uint NoDataTimeout;
    public uint CompletionTimeout;
}

/// <summary>CFUUIDBytes, passed by value to COM-style QueryInterface.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CFUUIDBytes
{
    public fixed byte Bytes[16];

    public CFUUIDBytes(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < 16; i++) Bytes[i] = bytes[i];
    }
}
