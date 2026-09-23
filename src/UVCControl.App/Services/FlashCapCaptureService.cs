using System.Globalization;
using FlashCap;
using Microsoft.Extensions.Logging;
using UVCControl.Core.Transport;

namespace UVCControl.App.Services;

/// <summary>Video capture through FlashCap (V4L2 on Linux, DirectShow on Windows).</summary>
/// <remarks>Not used on macOS, where FlashCap's AVFoundation backend is unreliable; see <c>AVFoundationCaptureService</c>.</remarks>
internal sealed class FlashCapCaptureService(ILogger<FlashCapCaptureService> logger) : ICameraCaptureService
{
    private readonly CaptureDevices _devices = new();

    public Task<ICameraSource?> FindAsync(UvcDeviceInfo device, CancellationToken ct = default) => Task.Run(() =>
    {
        var descriptors = _devices.EnumerateDescriptors()
            .Where(d => Matches(d, device))
            .OrderBy(d => d.DeviceType == DeviceTypes.VideoForWindows) // prefer DirectShow over VfW
            .ToList();
        logger.LogDebug("Capture devices for {Device}: {Names}", device.Name, string.Join(", ", descriptors.Select(d => d.Name)));
        return descriptors.Count > 0 ? (ICameraSource)new FlashCapSource(descriptors[0]) : null;
    }, ct);

    private static bool Matches(CaptureDeviceDescriptor descriptor, UvcDeviceInfo device)
    {
        var identity = descriptor.Identity?.ToString() ?? "";
        // Linux: the V4L2 node; Windows: the DirectShow device path.
        if (device.CaptureHint is { } hint && string.Equals(identity, hint, StringComparison.OrdinalIgnoreCase)) return true;
        // Windows: \\?\usb#vid_2ca3&pid_0023&mi_00#…
        var vidPid = string.Create(CultureInfo.InvariantCulture, $"vid_{device.VendorId:x4}&pid_{device.ProductId:x4}");
        return identity.Contains(vidPid, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FlashCapSource : ICameraSource
    {
        private readonly CaptureDeviceDescriptor _descriptor;
        private readonly Dictionary<(int FormatId, double Rate), VideoCharacteristics> _characteristics = [];

        public FlashCapSource(CaptureDeviceDescriptor descriptor)
        {
            _descriptor = descriptor;
            var groups = descriptor.Characteristics
                .Where(c => c.PixelFormat != PixelFormats.Unknown)
                .GroupBy(c => (c.Width, c.Height, c.PixelFormat))
                .ToList();
            var formats = new List<CameraFormat>();
            foreach (var (group, id) in groups.Select((g, i) => (g, i)))
            {
                foreach (var c in group)
                    _characteristics.TryAdd((id, Round((double)c.FramesPerSecond)), c);
                var rates = group.Select(c => Round((double)c.FramesPerSecond)).Distinct().OrderDescending().ToList();
                formats.Add(new CameraFormat(id, group.Key.Width, group.Key.Height, group.Key.PixelFormat.ToString(), rates));
            }
            Formats = formats;
        }

        public string Name => _descriptor.Name;

        public IReadOnlyList<CameraFormat> Formats { get; }

        public async Task<IAsyncDisposable> StartAsync(CameraFormat format, double frameRate, IFrameSink sink,
                                                       CancellationToken ct = default)
        {
            if (!_characteristics.TryGetValue((format.Id, Round(frameRate)), out var characteristics))
                characteristics = _characteristics.First(kv => kv.Key.FormatId == format.Id).Value;

            // Raw formats arrive as (transcoded) BMP, MJPEG as JPEG.
            var device = await _descriptor.OpenAsync(characteristics, TranscodeFormats.Auto, isScattering: false,
                maxQueuingFrames: 1, scope => sink.WriteEncoded(scope.Buffer.ReferImage()), ct);
            try
            {
                await device.StartAsync(ct);
            }
            catch
            {
                await device.DisposeAsync();
                throw;
            }
            return new Session(device);
        }

        private static double Round(double rate) => Math.Round(rate, 2);

        public void Dispose() { }
    }

    private sealed class Session(CaptureDevice device) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await device.StopAsync();
            }
            finally
            {
                await device.DisposeAsync();
            }
        }
    }
}
