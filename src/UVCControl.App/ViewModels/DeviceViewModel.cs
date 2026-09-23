using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using UVCControl.Core.Devices;

namespace UVCControl.App.ViewModels;

/// <summary>A camera in the sidebar and, while selected, its detail page.</summary>
/// <remarks>
/// Every camera is connected and probed as soon as it's found (<see cref="PrepareAsync"/>) and stays connected,
/// so switching cameras is instant. Only the selected one runs a preview or live refresh.
/// </remarks>
public sealed partial class DeviceViewModel(UvcDevice device, IViewModelFactory factory, IOptions<UvcOptions> options)
    : ViewModelBase, IDisposable
{
    private bool _active;
    private CancellationTokenSource? _liveCts;
    private Task? _prepare;

    public UvcDevice Device => device;
    public string Name => device.Name;
    public string VidPid => device.VidPidString;
    public string Location => device.Info.Location;

    public ObservableCollection<UnitSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    public partial string UvcVersion { get; private set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial PtzViewModel? Ptz { get; private set; }

    /// <summary>Connection state for the sidebar.</summary>
    public string StatusText => IsLoading ? "Connecting…"
        : Error is not null ? "Unavailable"
        : Ptz is null ? ""
        : Ptz.HasPanTilt ? "PTZ ready"
        : Ptz.HasZoom ? "Zoom only"
        : "No PTZ";

    [ObservableProperty]
    public partial bool ShowPreview { get; set; } = true;

    /// <summary>Re-read all values periodically.</summary>
    [ObservableProperty]
    public partial bool AutoRefresh { get; set; }

    [ObservableProperty]
    public partial CameraPreviewViewModel? Preview { get; private set; }

    /// <summary>Opens and probes the camera once; later calls return the same task. Call on the UI thread.</summary>
    public Task PrepareAsync()
    {
        // A camera that failed to open gets another try.
        if (_prepare is { IsCompleted: true } && !device.IsDiscovered) _prepare = null;
        return _prepare ??= DiscoverAsync();
    }

    private async Task DiscoverAsync()
    {
        IsLoading = true;
        try
        {
            await device.DiscoverAsync();
        }
        finally
        {
            IsLoading = false;
        }
        BuildSections();
        if (device.IsDiscovered && Ptz is null) Ptz = factory.CreatePtz(device);
    }

    public async Task ActivateAsync()
    {
        if (_active) return;
        _active = true;
        device.Refreshed += OnDeviceRefreshed;
        UpdatePreview();
        UpdateLiveRefresh();

        if (_prepare is { IsCompleted: true } && device.IsDiscovered)
        {
            SyncAll();
            // Already connected: show it right away and catch up with the camera in the background.
            _ = device.RefreshAsync();
        }
        else
        {
            await PrepareAsync();
        }
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        device.Refreshed -= OnDeviceRefreshed;
        Ptz?.Deactivate();
        UpdatePreview();
        UpdateLiveRefresh();
    }

    [RelayCommand]
    private Task RefreshAsync() => device.RefreshAsync();

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        await device.ResetAllToDefaultsAsync();
        SyncAll();
    }

    partial void OnShowPreviewChanged(bool value) => UpdatePreview();

    partial void OnAutoRefreshChanged(bool value) => UpdateLiveRefresh();

    private void BuildSections()
    {
        Sections.Clear();
        foreach (var section in device.Sections) Sections.Add(new UnitSectionViewModel(section));
        UvcVersion = device.IsDiscovered ? device.Topology.VersionString : "—";
        Error = device.Error;
    }

    private void OnDeviceRefreshed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(SyncAll);

    private void SyncAll()
    {
        foreach (var control in Sections.SelectMany(s => s.Controls)) control.Sync();
        Ptz?.Sync();
    }

    private void UpdatePreview()
    {
        var wanted = _active && ShowPreview;
        if (wanted && Preview is null)
        {
            Preview = factory.CreatePreview(device.Info);
            _ = Preview.StartAsync();
        }
        else if (!wanted && Preview is { } preview)
        {
            Preview = null;
            _ = preview.DisposeAsync().AsTask();
        }
    }

    private void UpdateLiveRefresh()
    {
        _liveCts?.Cancel();
        _liveCts = null;
        if (!_active || !AutoRefresh) return;

        var cts = _liveCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(options.Value.LiveRefreshInterval);
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                await device.RefreshAsync().ConfigureAwait(false);
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default); // swallow cancellation
    }

    public void Dispose()
    {
        Deactivate();
        if (Ptz is { } ptz) _ = ptz.DisposeAsync().AsTask();
    }
}
