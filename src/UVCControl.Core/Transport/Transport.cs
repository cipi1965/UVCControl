using UVCControl.Core.Descriptors;

namespace UVCControl.Core.Transport;

/// <summary>UVC class-specific request codes (UVC 1.5 table A-8).</summary>
public enum UvcRequest : byte
{
    SetCur = 0x01,
    GetCur = 0x81,
    GetMin = 0x82,
    GetMax = 0x83,
    GetRes = 0x84,
    GetLen = 0x85,
    GetInfo = 0x86,
    GetDef = 0x87,
}

public class UvcException(string message) : Exception(message);

/// <summary>An attached camera as found by a backend, before it is opened.</summary>
/// <param name="Id">Stable identifier while the device stays connected.</param>
/// <param name="Location">Human-readable bus location.</param>
/// <param name="CaptureHint">Backend-specific hint for finding the matching video capture device (e.g. /dev/video0).</param>
public sealed record UvcDeviceInfo(
    string Id,
    string Name,
    int VendorId,
    int ProductId,
    string Location,
    string? CaptureHint = null)
{
    /// <summary>Platform-specific data the backend needs to open the device.</summary>
    public object? PlatformData { get; init; }
}

/// <summary>Finds UVC cameras and opens them on one operating system.</summary>
public interface IUvcBackend
{
    string Name { get; }

    IReadOnlyList<UvcDeviceInfo> Enumerate();

    /// <exception cref="UvcException">The device could not be opened.</exception>
    IUvcTransport Open(UvcDeviceInfo device);
}

/// <summary>
/// Issues UVC requests to one camera. Implementations are only ever called from a single thread.
/// </summary>
public interface IUvcTransport : IDisposable
{
    /// <summary>Reads (or synthesizes) the VideoControl topology.</summary>
    /// <exception cref="UvcException">The descriptors could not be read.</exception>
    UvcTopology ReadTopology();

    /// <summary>Issues a GET_* request and returns the bytes the device sent back.</summary>
    /// <exception cref="UvcException">The device rejected the request.</exception>
    byte[] Get(UvcRequest request, byte unitId, byte selector, int length);

    /// <summary>Issues SET_CUR.</summary>
    /// <exception cref="UvcException">The device rejected the request.</exception>
    void Set(byte unitId, byte selector, byte[] data);
}

/// <summary>Used on platforms without a UVC backend.</summary>
internal sealed class UnsupportedUvcBackend : IUvcBackend
{
    public string Name => "Unsupported platform";
    public IReadOnlyList<UvcDeviceInfo> Enumerate() => [];
    public IUvcTransport Open(UvcDeviceInfo device) => throw new UvcException("This platform is not supported.");
}
