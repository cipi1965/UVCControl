using UVCControl.Core.Transport;

namespace UVCControl.App.Services;

/// <summary>A capture format with the frame rates the camera offers for it.</summary>
/// <param name="Id">Backend-specific index of the format.</param>
/// <param name="PixelFormat">Native pixel format, e.g. a FourCC like "420v" or "YUYV".</param>
public sealed record CameraFormat(int Id, int Width, int Height, string PixelFormat, IReadOnlyList<double> FrameRates)
{
    public string Label => $"{Width} × {Height} · {PixelFormat}";
}

/// <summary>Receives frames on a capture thread.</summary>
public interface IFrameSink
{
    /// <summary>Top-down BGRA pixels; <paramref name="stride"/> may exceed width × 4.</summary>
    void WriteBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride);

    /// <summary>A BMP, JPEG or PNG encoded frame.</summary>
    void WriteEncoded(ReadOnlySpan<byte> image);
}

/// <summary>The video side of a UVC camera.</summary>
public interface ICameraSource : IDisposable
{
    string Name { get; }

    IReadOnlyList<CameraFormat> Formats { get; }

    /// <summary>Starts streaming into <paramref name="sink"/>; dispose the result to stop.</summary>
    Task<IAsyncDisposable> StartAsync(CameraFormat format, double frameRate, IFrameSink sink, CancellationToken ct = default);
}

public interface ICameraCaptureService
{
    /// <summary>Finds the video capture device that belongs to a UVC camera.</summary>
    Task<ICameraSource?> FindAsync(UvcDeviceInfo device, CancellationToken ct = default);
}
