using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using UVCControl.Core.Descriptors;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Platform.Windows;

/// <summary>Finds USB video devices through DirectShow and drives them with IAMCameraControl / IAMVideoProcAmp.</summary>
/// <remarks>
/// usbvideo.sys owns the device, so raw class requests aren't possible. Standard Camera Terminal and
/// Processing Unit controls are emulated on top of the DirectShow property sets; extension units are not
/// available with this backend.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsUvcBackend : IUvcBackend
{
    public string Name => "Windows DirectShow";

    public IReadOnlyList<UvcDeviceInfo> Enumerate()
    {
        var found = new List<UvcDeviceInfo>();
        foreach (var (moniker, name, path) in EnumerateMonikers())
        {
            Marshal.ReleaseComObject(moniker);
            var match = VidPidRegex().Match(path);
            if (!match.Success) continue;
            found.Add(new UvcDeviceInfo(
                Id: path,
                Name: name,
                VendorId: Convert.ToInt32(match.Groups[1].Value, 16),
                ProductId: Convert.ToInt32(match.Groups[2].Value, 16),
                Location: path,
                CaptureHint: path));
        }
        return found;
    }

    public IUvcTransport Open(UvcDeviceInfo device)
    {
        foreach (var (moniker, _, path) in EnumerateMonikers())
        {
            try
            {
                if (!string.Equals(path, device.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var iid = DirectShow.IID_IBaseFilter;
                moniker.BindToObject(null!, null!, ref iid, out var filter);
                return new WindowsUvcTransport(filter);
            }
            catch (COMException e)
            {
                throw new UvcException($"Could not open the capture filter: 0x{e.HResult:X8}");
            }
            finally
            {
                Marshal.ReleaseComObject(moniker);
            }
        }
        throw new UvcException("Device is no longer connected.");
    }

    private static IEnumerable<(IMoniker Moniker, string Name, string Path)> EnumerateMonikers()
    {
        var type = Type.GetTypeFromCLSID(DirectShow.CLSID_SystemDeviceEnum)
                   ?? throw new UvcException("DirectShow is not available.");
        var devEnum = (ICreateDevEnum)Activator.CreateInstance(type)!;
        try
        {
            var category = DirectShow.CLSID_VideoInputDeviceCategory;
            // S_FALSE (1) means the category is empty.
            if (devEnum.CreateClassEnumerator(ref category, out var enumMoniker, 0) != 0 || enumMoniker is null) yield break;
            try
            {
                var monikers = new IMoniker[1];
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    var (name, path) = ReadProperties(moniker);
                    if (path is null)
                    {
                        Marshal.ReleaseComObject(moniker);
                        continue;
                    }
                    yield return (moniker, name ?? "USB Camera", path);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumMoniker);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(devEnum);
        }
    }

    private static (string? Name, string? Path) ReadProperties(IMoniker moniker)
    {
        var bagId = typeof(IPropertyBag).GUID;
        moniker.BindToStorage(null!, null!, ref bagId, out var bagObject);
        var bag = (IPropertyBag)bagObject;
        try
        {
            var name = bag.Read("FriendlyName", out var n, IntPtr.Zero) == 0 ? n as string : null;
            var path = bag.Read("DevicePath", out var p, IntPtr.Zero) == 0 ? p as string : null;
            return (name, path);
        }
        finally
        {
            Marshal.ReleaseComObject(bag);
        }
    }

    [GeneratedRegex("vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsUvcTransport : IUvcTransport
{
    // Synthetic unit IDs: DirectShow hides the real descriptor topology.
    private const byte CameraTerminalId = 1;
    private const byte ProcessingUnitId = 2;

    private object? _filter;

    public WindowsUvcTransport(object filter)
    {
        _filter = filter;
        if (filter is IAMCameraControl camera) Camera = new CameraControlSet(camera);
        if (filter is IAMVideoProcAmp procAmp) ProcAmp = new VideoProcAmpSet(procAmp);
    }

    internal IDirectShowPropertySet? Camera { get; }
    internal IDirectShowPropertySet? ProcAmp { get; }

    public UvcTopology ReadTopology()
    {
        // All bits set: DirectShow answers only for controls the driver supports, so nothing is "unadvertised".
        byte[] all = [0xFF, 0xFF, 0xFF];
        var topology = new UvcTopology();
        if (Camera is not null) topology.CameraTerminals.Add(new UvcTopology.Terminal(CameraTerminalId, 0x0201, all));
        if (ProcAmp is not null) topology.ProcessingUnits.Add(new UvcTopology.ProcessingUnit(ProcessingUnitId, all));
        return topology;
    }

    public byte[] Get(UvcRequest request, byte unitId, byte selector, int length) =>
        Mapping(unitId, selector).Get(this, request, length);

    public void Set(byte unitId, byte selector, byte[] data) => Mapping(unitId, selector).Set(this, data);

    private DirectShowMapping Mapping(byte unitId, byte selector)
    {
        var table = unitId switch
        {
            CameraTerminalId => DirectShowMapping.CameraTerminal,
            ProcessingUnitId => DirectShowMapping.ProcessingUnit,
            _ => null,
        };
        return table?.GetValueOrDefault(selector) ?? throw new UvcException("Not available through DirectShow");
    }

    public void Dispose()
    {
        if (_filter is null) return;
        Marshal.ReleaseComObject(_filter);
        _filter = null;
    }
}
