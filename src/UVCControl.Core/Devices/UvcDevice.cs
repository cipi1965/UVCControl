using Microsoft.Extensions.Logging;
using UVCControl.Core.Controls;
using UVCControl.Core.Descriptors;
using UVCControl.Core.Threading;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Devices;

public enum UnitKind
{
    CameraTerminal,
    ProcessingUnit,
    ExtensionUnit,
}

public sealed record UnitSection(byte UnitId, UnitKind Kind, Guid? ExtensionGuid, IReadOnlyList<UvcControl> Controls)
{
    public string Title => Kind switch
    {
        UnitKind.CameraTerminal => "Camera Terminal",
        UnitKind.ProcessingUnit => "Processing Unit",
        _ => "Extension Unit",
    };
}

/// <summary>A UVC camera: its topology, the controls it answers, and a serial I/O thread for talking to it.</summary>
public sealed class UvcDevice : IDisposable
{
    private readonly IUvcBackend _backend;
    private readonly ILogger _logger;
    private readonly TimeSpan _refreshDelay;
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

    internal UvcDevice(UvcDeviceInfo info, IUvcBackend backend, ILogger logger, TimeSpan refreshDelay)
    {
        Info = info;
        _backend = backend;
        _logger = logger;
        _refreshDelay = refreshDelay;
        Executor = new SerialExecutor($"UVC {info.Name}");
    }

    public UvcDeviceInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public int VendorId => Info.VendorId;
    public int ProductId => Info.ProductId;
    public string VidPidString => $"{VendorId:X4}:{ProductId:X4}";

    public UvcTopology Topology { get; private set; } = UvcTopology.Empty;
    public IReadOnlyList<UnitSection> Sections { get; private set; } = [];
    public string? Error { get; private set; }
    public bool IsDiscovered { get; private set; }

    public IEnumerable<UvcControl> AllControls => Sections.SelectMany(s => s.Controls);

    /// <summary>Raised on the I/O thread after values were re-read from the device.</summary>
    public event EventHandler? Refreshed;

    internal SerialExecutor Executor { get; }
    internal ILogger Logger => _logger;
    internal IUvcTransport? Transport { get; private set; }

    internal IUvcTransport RequireTransport() => Transport ?? throw new UvcException("No device");

    /// <summary>
    /// Reads descriptors and probes every standard selector on every unit,
    /// including ones the device doesn't advertise.
    /// </summary>
    public Task DiscoverAsync() => Executor.InvokeAsync(Discover);

    /// <summary>Re-reads GET_INFO and GET_CUR for every control.</summary>
    public Task RefreshAsync() => Executor.InvokeAsync(RefreshNow);

    /// <summary>Resets every writable absolute control to its default.</summary>
    public Task ResetAllToDefaultsAsync() => Executor.InvokeAsync(() =>
    {
        foreach (var control in AllControls.Where(c => c.SupportsSet && c.Definition.IsAbsolute))
            control.ResetToDefaultNow();
    });

    private void Discover()
    {
        try
        {
            Transport ??= _backend.Open(Info);
        }
        catch (UvcException e)
        {
            _logger.LogWarning("Opening {Device} failed: {Message}", Name, e.Message);
            Error = $"Could not open the USB device: {e.Message}";
            return;
        }

        try
        {
            Topology = Transport.ReadTopology();
        }
        catch (UvcException e)
        {
            Error = $"Reading configuration descriptor failed: {e.Message}";
            return;
        }

        var sections = new List<UnitSection>();
        foreach (var ct in Topology.CameraTerminals)
        {
            var controls = Probe(UvcControlCatalog.CameraTerminal.Select(def =>
                new UvcControl(this, ct.Id, UnitKind.CameraTerminal, def,
                    UvcControlCatalog.IsAdvertised(ct.BmControls, UvcControlCatalog.CameraTerminalBits.GetValueOrDefault(def.Selector, -1)))));
            sections.Add(new UnitSection(ct.Id, UnitKind.CameraTerminal, null, controls));
        }
        foreach (var pu in Topology.ProcessingUnits)
        {
            var controls = Probe(UvcControlCatalog.ProcessingUnit.Select(def =>
                new UvcControl(this, pu.Id, UnitKind.ProcessingUnit, def,
                    UvcControlCatalog.IsAdvertised(pu.BmControls, UvcControlCatalog.ProcessingUnitBits.GetValueOrDefault(def.Selector, -1)))));
            sections.Add(new UnitSection(pu.Id, UnitKind.ProcessingUnit, null, controls));
        }
        foreach (var xu in Topology.ExtensionUnits)
        {
            // Probe every selector the bitmap could describe, not just the set bits.
            var upper = Math.Clamp(Math.Max(xu.NumControls, xu.BmControls.Length * 8), 1, 255);
            var controls = Probe(Enumerable.Range(1, upper).Select(selector =>
                new UvcControl(this, xu.Id, UnitKind.ExtensionUnit, ControlDefinition.Raw((byte)selector, 0),
                    UvcControlCatalog.IsAdvertised(xu.BmControls, selector - 1))));
            sections.Add(new UnitSection(xu.Id, UnitKind.ExtensionUnit, xu.Guid, controls));
        }

        Sections = sections;
        Error = null;
        IsDiscovered = true;
        _logger.LogInformation("Discovered {Count} controls on {Device} (UVC {Version})",
            sections.Sum(s => s.Controls.Count), Name, Topology.VersionString);
    }

    private List<UvcControl> Probe(IEnumerable<UvcControl> candidates) =>
        candidates.Where(c => c.Probe(Transport!)).ToList();

    private void RefreshNow()
    {
        foreach (var control in AllControls) control.ReadCurrent(force: false);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refreshes shortly after a change, so auto modes and dependent flags catch up.</summary>
    internal void ScheduleRefresh()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        _ = Task.Delay(_refreshDelay, cts.Token).ContinueWith(
            _ => Executor.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && !_disposed) RefreshNow();
            }),
            cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshCts?.Cancel();
        _ = Executor.InvokeAsync(() =>
        {
            Transport?.Dispose();
            Transport = null;
        });
        Executor.Dispose();
    }
}
