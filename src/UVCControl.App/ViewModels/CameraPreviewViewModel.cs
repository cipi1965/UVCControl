using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using UVCControl.App.Services;
using UVCControl.Core.Transport;

namespace UVCControl.App.ViewModels;

/// <summary>Live video plus the choice of capture format and frame rate.</summary>
public sealed partial class CameraPreviewViewModel(
    UvcDeviceInfo device,
    ICameraCaptureService capture,
    ISettingsStore settings,
    ILogger<CameraPreviewViewModel> logger) : ViewModelBase, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly PreviewFrameBuffer _frames = new();
    private ICameraSource? _source;
    private IAsyncDisposable? _session;
    private int _presentPending;
    private bool _updating;
    private int _generation;
    private int _framesSinceStart;
    private bool _stalled;

    /// <summary>How long to wait for the first frame before telling the user the format isn't streaming.</summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(4);

    private string DefaultsKey => $"format.{device.VendorId}.{device.ProductId}";

    public ObservableCollection<CameraFormat> Formats { get; } = [];

    public ObservableCollection<double> FrameRates { get; } = [];

    [ObservableProperty]
    public partial CameraFormat? SelectedFormat { get; set; }

    [ObservableProperty]
    public partial double? SelectedFrameRate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Frame { get; private set; }

    [ObservableProperty]
    public partial bool HasFormats { get; private set; }

    public bool HasMessage => Message is not null;

    public async Task StartAsync()
    {
        Message = "Starting preview…";
        _frames.FrameWritten += OnFrameWritten;
        try
        {
            _source = await capture.FindAsync(device, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Finding the capture device failed");
            Message = e.Message;
            return;
        }
        if (_cts.IsCancellationRequested) return;
        if (_source is null || _source.Formats.Count == 0)
        {
            Message = "No video device found for this camera.";
            return;
        }

        foreach (var format in _source.Formats) Formats.Add(format);
        HasFormats = true;

        var saved = settings.GetString(DefaultsKey);
        var initial = Formats.FirstOrDefault(f => f.Label == saved) ?? PreferredFormat();
        if (initial is null) return;
        Select(initial, settings.GetDouble(DefaultsKey + ".fps"));
        await RestartAsync();
    }

    partial void OnSelectedFormatChanged(CameraFormat? value)
    {
        if (_updating || value is null) return;
        Select(value, SelectedFrameRate);
        _ = RestartAsync();
    }

    partial void OnSelectedFrameRateChanged(double? value)
    {
        if (_updating || value is null) return;
        _ = RestartAsync();
    }

    /// <summary>Selects a format and the closest available frame rate without restarting capture.</summary>
    private void Select(CameraFormat format, double? frameRate)
    {
        _updating = true;
        SelectedFormat = format;
        FrameRates.Clear();
        foreach (var rate in format.FrameRates) FrameRates.Add(rate);
        SelectedFrameRate = frameRate is { } r && format.FrameRates.FirstOrDefault(x => Math.Abs(x - r) < 0.01) is var match && match > 0
            ? match
            : format.FrameRates.FirstOrDefault();
        _updating = false;
    }

    /// <summary>
    /// Largest landscape format up to 1080p. Some cameras (DJI Osmo Pocket) default to portrait,
    /// and larger frames are costly to copy for a preview.
    /// </summary>
    private CameraFormat? PreferredFormat()
    {
        var landscape = Formats.Where(f => f.Width >= f.Height).ToList();
        var candidates = landscape.Where(f => f.Width * f.Height <= 1920 * 1080).ToList();
        if (candidates.Count == 0) candidates = landscape;
        return candidates.MaxBy(f => f.Width * f.Height) ?? Formats.FirstOrDefault();
    }

    private async Task RestartAsync()
    {
        if (_source is null || SelectedFormat is not { } format || SelectedFrameRate is not { } rate) return;
        await _gate.WaitAsync();
        try
        {
            if (_cts.IsCancellationRequested) return;
            await StopSessionAsync();
            ClearFrame();
            Volatile.Write(ref _framesSinceStart, 0);
            var generation = ++_generation;
            _session = await _source.StartAsync(format, rate, _frames, _cts.Token);
            Message = null;
            _stalled = false;
            settings.Set(DefaultsKey, format.Label);
            settings.Set(DefaultsKey + ".fps", rate);
            _ = WatchFirstFrameAsync(generation, format);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Starting preview for {Device} failed", device.Name);
            Message = $"Could not start preview: {e.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopSessionAsync()
    {
        if (_session is null) return;
        var session = _session;
        _session = null;
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Stopping preview failed");
        }
    }

    /// <summary>
    /// Some advertised formats never deliver a frame (e.g. an H.264 stream macOS lists but doesn't decode);
    /// say so instead of showing a black preview forever.
    /// </summary>
    private async Task WatchFirstFrameAsync(int generation, CameraFormat format)
    {
        try
        {
            await Task.Delay(FirstFrameTimeout, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (generation != _generation || Volatile.Read(ref _framesSinceStart) > 0) return;
        logger.LogWarning("No frames from {Device} in {Format}", device.Name, format.Label);
        _stalled = true;
        Message = $"No frames arrived in {format.Label}. The camera doesn't stream this format here — try another one.";
    }

    private void ClearFrame()
    {
        var frame = Frame;
        Frame = null;
        if (!_frames.Owns(frame)) frame?.Dispose();
    }

    /// <summary>Capture thread: let the UI thread present at most one pending frame.</summary>
    private void OnFrameWritten()
    {
        Interlocked.Increment(ref _framesSinceStart);
        if (Interlocked.Exchange(ref _presentPending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _presentPending, 0);
            if (_cts.IsCancellationRequested) return;
            var previous = Frame;
            if (_frames.Present() is not { } bitmap) return;
            Frame = bitmap;
            if (_stalled)
            {
                _stalled = false;
                Message = null;
            }
            // Decoded JPEG frames are fresh bitmaps; the uncompressed ones are recycled by the buffer.
            if (previous is not null && previous != bitmap && !_frames.Owns(previous)) previous.Dispose();
        }, DispatcherPriority.Render);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            await StopSessionAsync();
        }
        finally
        {
            _gate.Release();
        }
        _frames.FrameWritten -= OnFrameWritten;
        _source?.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearFrame();
            _frames.Dispose();
        });
    }
}
