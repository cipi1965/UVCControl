using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Devices;

public sealed class UvcOptions
{
    public const string SectionName = "Uvc";

    /// <summary>Delay before re-reading every control after a write, so auto modes catch up.</summary>
    public TimeSpan RefreshAfterWrite { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Interval of the "Live" auto-refresh.</summary>
    public TimeSpan LiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public interface IUvcDeviceManager
{
    string BackendName { get; }

    IReadOnlyList<UvcDevice> Devices { get; }

    /// <summary>Re-enumerates cameras. Devices that are still connected keep their instance, so UI state survives.</summary>
    Task<IReadOnlyList<UvcDevice>> ReloadAsync();
}

internal sealed class UvcDeviceManager(IUvcBackend backend, IOptions<UvcOptions> options, ILoggerFactory loggerFactory)
    : IUvcDeviceManager, IDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UvcDeviceManager>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<UvcDevice> _devices = [];

    public string BackendName => backend.Name;

    public IReadOnlyList<UvcDevice> Devices => _devices;

    public async Task<IReadOnlyList<UvcDevice>> ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var infos = await Task.Run(() =>
            {
                try
                {
                    return backend.Enumerate();
                }
                catch (Exception e) when (e is UvcException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(e, "Enumerating UVC devices failed");
                    return [];
                }
            });

            var found = infos
                .DistinctBy(i => i.Id)
                .Select(info => _devices.FirstOrDefault(d => d.Id == info.Id)
                                ?? new UvcDevice(info, backend, loggerFactory.CreateLogger<UvcDevice>(), options.Value.RefreshAfterWrite))
                .ToList();

            foreach (var gone in _devices.Except(found)) gone.Dispose();
            _devices = found;
            _logger.LogInformation("{Backend}: found {Count} UVC device(s)", backend.Name, found.Count);
            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var device in _devices) device.Dispose();
        _devices = [];
    }
}
