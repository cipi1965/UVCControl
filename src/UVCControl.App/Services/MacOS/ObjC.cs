using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UVCControl.App.Services.MacOS;

/// <summary>Minimal Objective-C runtime access.</summary>
/// <remarks>
/// Ownership follows Cocoa rules: objects from alloc/new/copy are owned (+1) and must be <see cref="Release"/>d;
/// everything else is autoreleased and must be <see cref="Retain"/>ed to outlive the current
/// <see cref="AutoreleasePool"/>. Always create objects inside a pool, or they leak into the thread's
/// implicit pool and are released when that thread exits.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjC
{
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    public static readonly IntPtr MsgSendPtr;
    public static readonly IntPtr MsgSendStretPtr;

    static ObjC()
    {
        var lib = NativeLibrary.Load(ObjCLib);
        MsgSendPtr = NativeLibrary.GetExport(lib, "objc_msgSend");
        // Structs larger than 16 bytes are returned through objc_msgSend_stret on x86-64 only.
        MsgSendStretPtr = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? NativeLibrary.GetExport(lib, "objc_msgSend_stret")
            : MsgSendPtr;
    }

    [LibraryImport(ObjCLib, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetClass(string name);

    [LibraryImport(ObjCLib, EntryPoint = "objc_getProtocol", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProtocol(string name);

    [LibraryImport(ObjCLib, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr Selector(string name);

    [LibraryImport(ObjCLib, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr AllocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(ObjCLib, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClassPair(IntPtr cls);

    [LibraryImport(ObjCLib, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [LibraryImport(ObjCLib, EntryPoint = "class_addProtocol")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddProtocol(IntPtr cls, IntPtr protocol);

    [LibraryImport(ObjCLib, EntryPoint = "objc_retain")]
    public static partial IntPtr Retain(IntPtr obj);

    [LibraryImport(ObjCLib, EntryPoint = "objc_release")]
    public static partial void Release(IntPtr obj);

    [LibraryImport(ObjCLib, EntryPoint = "objc_autoreleasePoolPush")]
    private static partial IntPtr AutoreleasePoolPush();

    [LibraryImport(ObjCLib, EntryPoint = "objc_autoreleasePoolPop")]
    private static partial void AutoreleasePoolPop(IntPtr pool);

    public static AutoreleasePool Pool() => new(AutoreleasePoolPush());

    public readonly struct AutoreleasePool(IntPtr handle) : IDisposable
    {
        public void Dispose() => AutoreleasePoolPop(handle);
    }

    // Typed objc_msgSend shapes.

    public static IntPtr Send(IntPtr obj, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSendPtr)(obj, Selector(sel));

    public static IntPtr Send(IntPtr obj, string sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)MsgSendPtr)(obj, Selector(sel), a);

    public static IntPtr Send(IntPtr obj, string sel, IntPtr a, IntPtr b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSendPtr)(obj, Selector(sel), a, b);

    public static IntPtr Send(IntPtr obj, string sel, IntPtr a, IntPtr b, nint c) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, nint, IntPtr>)MsgSendPtr)(obj, Selector(sel), a, b, c);

    public static IntPtr SendIndex(IntPtr obj, string sel, nuint index) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr>)MsgSendPtr)(obj, Selector(sel), index);

    public static nuint SendNUInt(IntPtr obj, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nuint>)MsgSendPtr)(obj, Selector(sel));

    public static nint SendNInt(IntPtr obj, string sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint>)MsgSendPtr)(obj, Selector(sel), a);

    public static double SendDouble(IntPtr obj, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, double>)MsgSendPtr)(obj, Selector(sel));

    public static bool SendBool(IntPtr obj, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte>)MsgSendPtr)(obj, Selector(sel)) != 0;

    public static bool SendBool(IntPtr obj, string sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)MsgSendPtr)(obj, Selector(sel), a) != 0;

    public static void SendVoid(IntPtr obj, string sel, bool a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte, void>)MsgSendPtr)(obj, Selector(sel), (byte)(a ? 1 : 0));

    public static CMTime SendTime(IntPtr obj, string sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CMTime>)MsgSendStretPtr)(obj, Selector(sel));

    public static void SendVoid(IntPtr obj, string sel, CMTime a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CMTime, void>)MsgSendPtr)(obj, Selector(sel), a);

    /// <summary>Creates an autoreleased NSString.</summary>
    public static IntPtr NSString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = utf8)
            return Send(GetClass("NSString"), "stringWithUTF8String:", (IntPtr)p);
    }

    public static string? ToManagedString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    /// <summary>Reads an exported <c>NSString * const</c> symbol.</summary>
    public static IntPtr StringConstant(IntPtr library, string symbol) =>
        NativeLibrary.TryGetExport(library, symbol, out var address) ? *(IntPtr*)address : IntPtr.Zero;

    public static IEnumerable<IntPtr> Items(IntPtr nsArray)
    {
        var count = nsArray == IntPtr.Zero ? 0 : SendNUInt(nsArray, "count");
        for (nuint i = 0; i < count; i++) yield return SendIndex(nsArray, "objectAtIndex:", i);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMTime
{
    public long Value;
    public int Timescale;
    public uint Flags;
    public long Epoch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMVideoDimensions
{
    public int Width;
    public int Height;
}

/// <summary>CoreMedia, CoreVideo and libdispatch functions used by the capture pipeline.</summary>
[SupportedOSPlatform("macos")]
internal static partial class MediaNative
{
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    public const uint PixelFormat32BGRA = 0x42475241; // 'BGRA'
    public const ulong LockReadOnly = 1;

    [LibraryImport(CoreMedia)]
    public static partial CMVideoDimensions CMVideoFormatDescriptionGetDimensions(IntPtr description);

    [LibraryImport(CoreMedia)]
    public static partial uint CMFormatDescriptionGetMediaSubType(IntPtr description);

    [LibraryImport(CoreMedia)]
    public static partial IntPtr CMSampleBufferGetImageBuffer(IntPtr sampleBuffer);

    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferLockBaseAddress(IntPtr pixelBuffer, ulong flags);

    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferUnlockBaseAddress(IntPtr pixelBuffer, ulong flags);

    [LibraryImport(CoreVideo)]
    public static partial IntPtr CVPixelBufferGetBaseAddress(IntPtr pixelBuffer);

    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetWidth(IntPtr pixelBuffer);

    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetHeight(IntPtr pixelBuffer);

    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetBytesPerRow(IntPtr pixelBuffer);

    [LibraryImport(CoreVideo)]
    public static partial uint CVPixelBufferGetPixelFormatType(IntPtr pixelBuffer);

    [LibraryImport(LibSystem, EntryPoint = "dispatch_queue_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr DispatchQueueCreate(string label, IntPtr attributes);

    [LibraryImport(LibSystem, EntryPoint = "dispatch_release")]
    public static partial void DispatchRelease(IntPtr obj);
}
