using System.Globalization;
using System.Runtime.Versioning;
using UVCControl.Core.Descriptors;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Platform.Linux;

internal sealed record LinuxDeviceData(string VideoNode, string UsbSysfsPath);

/// <summary>Finds UVC cameras through sysfs and talks to them through their uvcvideo V4L2 node.</summary>
/// <remarks>
/// The uvcvideo driver keeps the VideoControl interface claimed, so raw USB access is not possible
/// without detaching it. Extension units are reached with UVCIOC_CTRL_QUERY; Camera Terminal and
/// Processing Unit requests are translated to the V4L2 controls the driver maps them to.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxUvcBackend : IUvcBackend
{
    private const string VideoClassPath = "/sys/class/video4linux";

    public string Name => "Linux V4L2 (uvcvideo)";

    public IReadOnlyList<UvcDeviceInfo> Enumerate()
    {
        if (!Directory.Exists(VideoClassPath)) return [];

        var byUsbDevice = new Dictionary<string, (string Node, bool Capture)>();
        var nodes = Directory.GetDirectories(VideoClassPath, "video*")
            .OrderBy(p => int.TryParse(Path.GetFileName(p)[5..], out var n) ? n : int.MaxValue);
        foreach (var classDir in nodes)
        {
            // …/video0/device → the USB interface uvcvideo is bound to (the VideoControl interface).
            var interfaceDir = ResolveLink(Path.Combine(classDir, "device"));
            if (interfaceDir is null) continue;
            if (ReadHex(interfaceDir, "bInterfaceClass") != 0x0E || ReadHex(interfaceDir, "bInterfaceSubClass") != 0x01) continue;
            var usbDir = Path.GetDirectoryName(interfaceDir);
            if (usbDir is null || !File.Exists(Path.Combine(usbDir, "descriptors"))) continue;

            var node = "/dev/" + Path.GetFileName(classDir);
            var capture = IsCaptureNode(node);
            if (!byUsbDevice.TryGetValue(usbDir, out var existing) || (capture && !existing.Capture))
                byUsbDevice[usbDir] = (node, capture);
        }

        return byUsbDevice.Select(entry =>
        {
            var (usbDir, (node, _)) = (entry.Key, entry.Value);
            var busName = Path.GetFileName(usbDir);
            return new UvcDeviceInfo(
                Id: busName,
                Name: ReadText(usbDir, "product") ?? "USB Camera",
                VendorId: ReadHex(usbDir, "idVendor") ?? 0,
                ProductId: ReadHex(usbDir, "idProduct") ?? 0,
                Location: $"Bus {ReadText(usbDir, "busnum")} Device {ReadText(usbDir, "devnum")} ({busName}, {node})",
                CaptureHint: node)
            {
                PlatformData = new LinuxDeviceData(node, usbDir),
            };
        }).ToList();
    }

    public IUvcTransport Open(UvcDeviceInfo device)
    {
        if (device.PlatformData is not LinuxDeviceData data) throw new UvcException("Not a V4L2 device.");
        var fd = LibC.Open(data.VideoNode, LibC.O_RDWR | LibC.O_NONBLOCK);
        if (fd < 0)
        {
            var errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            var hint = errno == LibC.EACCES ? " — add your user to the 'video' group" : "";
            throw new UvcException($"{data.VideoNode}: {LibC.ErrorString(errno)}{hint}");
        }
        return new LinuxUvcTransport(fd, data);
    }

    private static unsafe bool IsCaptureNode(string node)
    {
        var fd = LibC.Open(node, LibC.O_RDWR | LibC.O_NONBLOCK);
        if (fd < 0) return false;
        try
        {
            V4L2Capability cap;
            if (LibC.Ioctl(fd, V4L2.VIDIOC_QUERYCAP, &cap) < 0) return false;
            var caps = (cap.Capabilities & V4L2.V4L2_CAP_DEVICE_CAPS) != 0 ? cap.DeviceCaps : cap.Capabilities;
            return (caps & V4L2.V4L2_CAP_VIDEO_CAPTURE) != 0;
        }
        finally
        {
            LibC.Close(fd);
        }
    }

    private static string? ResolveLink(string path)
    {
        try
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadText(string dir, string file)
    {
        try
        {
            return File.ReadAllText(Path.Combine(dir, file)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadHex(string dir, string file) =>
        int.TryParse(ReadText(dir, file), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
}

[SupportedOSPlatform("linux")]
internal sealed unsafe class LinuxUvcTransport(int fd, LinuxDeviceData data) : IUvcTransport
{
    private int _fd = fd;
    private UvcTopology _topology = UvcTopology.Empty;

    public UvcTopology ReadTopology()
    {
        try
        {
            // Device descriptor followed by every configuration descriptor; the parser skips to the VC interface.
            _topology = UvcTopology.Parse(File.ReadAllBytes(Path.Combine(data.UsbSysfsPath, "descriptors")));
            return _topology;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UvcException(e.Message);
        }
    }

    public byte[] Get(UvcRequest request, byte unitId, byte selector, int length)
    {
        if (IsExtensionUnit(unitId))
        {
            var buffer = new byte[length];
            XuQuery(unitId, selector, request, buffer);
            return buffer;
        }
        return Mapping(unitId, selector).Get(this, request, length);
    }

    public void Set(byte unitId, byte selector, byte[] payload)
    {
        if (IsExtensionUnit(unitId))
            XuQuery(unitId, selector, UvcRequest.SetCur, (byte[])payload.Clone());
        else
            Mapping(unitId, selector).Set(this, payload);
    }

    private bool IsExtensionUnit(byte unitId) => _topology.ExtensionUnits.Any(xu => xu.Id == unitId);

    private V4L2Mapping Mapping(byte unitId, byte selector)
    {
        var table = _topology.CameraTerminals.Any(t => t.Id == unitId) ? V4L2Mapping.CameraTerminal
            : _topology.ProcessingUnits.Any(p => p.Id == unitId) ? V4L2Mapping.ProcessingUnit
            : null;
        return table?.GetValueOrDefault(selector) ?? throw new UvcException("Not mapped by uvcvideo");
    }

    private void XuQuery(byte unit, byte selector, UvcRequest request, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            var query = new UvcXuControlQuery
            {
                Unit = unit, Selector = selector, Query = (byte)request, Size = (ushort)buffer.Length, Data = (IntPtr)p,
            };
            Ioctl(V4L2.UVCIOC_CTRL_QUERY, &query);
        }
    }

    internal V4L2QueryCtrl QueryControl(uint id)
    {
        var q = new V4L2QueryCtrl { Id = id };
        Ioctl(V4L2.VIDIOC_QUERYCTRL, &q);
        if ((q.Flags & V4L2.V4L2_CTRL_FLAG_DISABLED) != 0) throw new UvcException("Disabled");
        return q;
    }

    internal bool MenuItemExists(uint id, uint index)
    {
        var m = new V4L2QueryMenu { Id = id, Index = index };
        return LibC.Ioctl(_fd, V4L2.VIDIOC_QUERYMENU, &m) >= 0;
    }

    internal int GetControl(uint id)
    {
        var c = new V4L2Control { Id = id };
        Ioctl(V4L2.VIDIOC_G_CTRL, &c);
        return c.Value;
    }

    internal void SetControl(uint id, int value)
    {
        var c = new V4L2Control { Id = id, Value = value };
        Ioctl(V4L2.VIDIOC_S_CTRL, &c);
    }

    private void Ioctl(nuint request, void* arg)
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);
        if (LibC.Ioctl(_fd, request, arg) < 0)
            throw new UvcException(LibC.ErrorString(System.Runtime.InteropServices.Marshal.GetLastPInvokeError()));
    }

    public void Dispose()
    {
        if (_fd < 0) return;
        LibC.Close(_fd);
        _fd = -1;
    }
}
