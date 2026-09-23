using System.Runtime.Versioning;
using UVCControl.Core.Descriptors;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Platform.MacOS;

/// <summary>Talks to UVC cameras through IOKit's user-space USB device interface.</summary>
/// <remarks>
/// Control requests go to endpoint 0 of the device, so no interface has to be claimed and the
/// system's video driver can keep streaming.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacUvcBackend : IUvcBackend
{
    public string Name => "macOS IOKit";

    /// <summary>Finds every USB device with a UVC VideoControl interface (class 0x0E, subclass 0x01).</summary>
    public IReadOnlyList<UvcDeviceInfo> Enumerate()
    {
        var found = new List<UvcDeviceInfo>();
        var seen = new HashSet<ulong>();

        var matching = IOKit.IOServiceMatching("IOUSBHostInterface");
        if (IOKit.IOServiceGetMatchingServices(0, matching, out var iterator) != IOKit.KIOReturnSuccess) return found;
        try
        {
            uint intf;
            while ((intf = IOKit.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    if (IOKit.GetInt(intf, "bInterfaceClass") != 0x0E || IOKit.GetInt(intf, "bInterfaceSubClass") != 0x01) continue;
                    if (IOKit.IORegistryEntryGetParentEntry(intf, "IOService", out var parent) != IOKit.KIOReturnSuccess) continue;
                    try
                    {
                        var id = IOKit.RegistryId(parent);
                        if (!seen.Add(id)) continue;
                        var location = IOKit.GetInt(parent, "locationID") ?? 0;
                        found.Add(new UvcDeviceInfo(
                            Id: $"{id:X}",
                            Name: IOKit.GetString(parent, "USB Product Name") ?? IOKit.GetString(parent, "kUSBProductString") ?? "USB Camera",
                            VendorId: (int)(IOKit.GetInt(parent, "idVendor") ?? 0),
                            ProductId: (int)(IOKit.GetInt(parent, "idProduct") ?? 0),
                            Location: $"0x{location:X8}")
                        {
                            PlatformData = id,
                        });
                    }
                    finally
                    {
                        IOKit.IOObjectRelease(parent);
                    }
                }
                finally
                {
                    IOKit.IOObjectRelease(intf);
                }
            }
        }
        finally
        {
            IOKit.IOObjectRelease(iterator);
        }
        return found;
    }

    public IUvcTransport Open(UvcDeviceInfo device)
    {
        if (device.PlatformData is not ulong registryId) throw new UvcException("Not an IOKit device.");
        var service = IOKit.IOServiceGetMatchingService(0, IOKit.IORegistryEntryIDMatching(registryId));
        if (service == 0) throw new UvcException("Device is no longer connected.");
        try
        {
            return MacUsbTransport.Open(service);
        }
        finally
        {
            IOKit.IOObjectRelease(service);
        }
    }
}

/// <summary>Owns an IOUSBDeviceInterface182 and performs endpoint 0 control transfers.</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacUsbTransport : IUvcTransport
{
    // Slots in the IOUSBDeviceStruct182 function table (IOUSBLib.h), counting IUNKNOWN_C_GUTS.
    private const int SlotQueryInterface = 1;
    private const int SlotRelease = 3;
    private const int SlotGetConfigurationDescriptorPtr = 21;
    private const int SlotDeviceRequestTO = 30;

    private static readonly byte[] DeviceUserClientTypeId =
        [0x9d, 0xc7, 0xb7, 0x80, 0x9e, 0xc0, 0x11, 0xD4, 0xa5, 0x4f, 0x00, 0x0a, 0x27, 0x05, 0x28, 0x61];
    private static readonly byte[] CFPlugInInterfaceId =
        [0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4, 0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F];
    private static readonly byte[] DeviceInterfaceId182 =
        [0x15, 0x2f, 0xc4, 0x96, 0x48, 0x91, 0x11, 0xD5, 0x9d, 0x52, 0x00, 0x0a, 0x27, 0x80, 0x1e, 0x86];

    private IntPtr _device;
    private byte _interfaceNumber;

    private MacUsbTransport(IntPtr device) => _device = device;

    public static MacUsbTransport Open(uint service)
    {
        var result = IOKit.IOCreatePlugInInterfaceForService(service, ConstantUuid(DeviceUserClientTypeId),
            ConstantUuid(CFPlugInInterfaceId), out var plugin, out _);
        if (result != IOKit.KIOReturnSuccess || plugin == IntPtr.Zero)
            throw new UvcException($"Could not create plug-in: {IOKit.ErrorString(result)}");

        IntPtr device;
        try
        {
            var queryInterface = (delegate* unmanaged<IntPtr, CFUUIDBytes, IntPtr*, int>)Slot(plugin, SlotQueryInterface);
            var iid = new CFUUIDBytes(DeviceInterfaceId182);
            IntPtr intf = 0;
            var hr = queryInterface(plugin, iid, &intf);
            if (hr != 0 || intf == IntPtr.Zero) throw new UvcException($"QueryInterface failed: 0x{hr:X8}");
            device = intf;
        }
        finally
        {
            Release(plugin);
        }
        return new MacUsbTransport(device);
    }

    public UvcTopology ReadTopology()
    {
        var topology = UvcTopology.Parse(ConfigurationDescriptor());
        _interfaceNumber = topology.InterfaceNumber;
        return topology;
    }

    public byte[] Get(UvcRequest request, byte unitId, byte selector, int length)
    {
        var buffer = new byte[length];
        var actual = Control(0xA1, (byte)request, (ushort)(selector << 8), (ushort)(unitId << 8 | _interfaceNumber), buffer);
        return actual == length ? buffer : buffer[..actual];
    }

    public void Set(byte unitId, byte selector, byte[] data) =>
        Control(0x21, (byte)UvcRequest.SetCur, (ushort)(selector << 8), (ushort)(unitId << 8 | _interfaceNumber), data);

    private int Control(byte requestType, byte request, ushort value, ushort index, byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_device == IntPtr.Zero, this);
        var deviceRequest = (delegate* unmanaged<IntPtr, IOUSBDevRequestTO*, int>)Slot(_device, SlotDeviceRequestTO);
        fixed (byte* p = buffer)
        {
            var req = new IOUSBDevRequestTO
            {
                BmRequestType = requestType,
                BRequest = request,
                WValue = value,
                WIndex = index,
                WLength = (ushort)buffer.Length,
                PData = (IntPtr)p,
                NoDataTimeout = 1000,
                CompletionTimeout = 2000,
            };
            var result = deviceRequest(_device, &req);
            if (result != IOKit.KIOReturnSuccess) throw new UvcException(IOKit.ErrorString(result));
            return (int)req.WLenDone;
        }
    }

    private byte[] ConfigurationDescriptor()
    {
        var getDescriptor = (delegate* unmanaged<IntPtr, byte, IntPtr*, int>)Slot(_device, SlotGetConfigurationDescriptorPtr);
        IntPtr desc = 0;
        if (getDescriptor(_device, 0, &desc) == IOKit.KIOReturnSuccess && desc != IntPtr.Zero)
        {
            var header = (byte*)desc;
            var total = header[2] | header[3] << 8;
            return new ReadOnlySpan<byte>(header, total).ToArray();
        }

        // Fallback: explicit GET_DESCRIPTOR(CONFIGURATION) request.
        var head = new byte[9];
        Control(0x80, 0x06, 0x0200, 0, head);
        var full = new byte[head[2] | head[3] << 8];
        var done = Control(0x80, 0x06, 0x0200, 0, full);
        return full[..done];
    }

    private static IntPtr ConstantUuid(byte[] b) =>
        IOKit.CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero, b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
            b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);

    /// <summary>Reads a function pointer from a COM-style interface (<c>Interface **</c>).</summary>
    private static IntPtr Slot(IntPtr intf, int index) => (*(IntPtr**)intf)[index];

    private static void Release(IntPtr intf) => ((delegate* unmanaged<IntPtr, uint>)Slot(intf, SlotRelease))(intf);

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        Release(_device);
        _device = IntPtr.Zero;
    }
}
