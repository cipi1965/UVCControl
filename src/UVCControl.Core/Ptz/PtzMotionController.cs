using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UVCControl.Core.Devices;

namespace UVCControl.Core.Ptz;

/// <summary>
/// Moves a camera by streaming absolute positions: continuous jogging from a joystick or held buttons,
/// and eased moves to presets.
/// </summary>
/// <remarks>
/// A loop runs only while something moves. Each tick awaits its SET_CUR, so a slow camera drops ticks
/// instead of queueing a backlog on the device's I/O thread.
/// </remarks>
public sealed class PtzMotionController : IAsyncDisposable
{
    private const int AxisCount = 4;

    /// <summary>A tick longer than this (e.g. after a stall) is shortened so the camera never jumps.</summary>
    private const double MaxTickSeconds = 0.1;

    /// <summary>How long the loop idles before it stops and hands the axes back to refreshes.</summary>
    private const double IdleSeconds = 0.3;

    private readonly PtzAxes _axes;
    private readonly PtzOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly double[] _rates = new double[AxisCount];
    private readonly double[] _jog = new double[AxisCount];
    private readonly double[] _position = new double[AxisCount];
    private readonly long[] _sent = new long[AxisCount];
    private PtzMove? _move;
    private TaskCompletionSource<bool>? _moveDone;
    private Task _loop = Task.CompletedTask;
    private bool _running;
    private double _jogSpeed = 0.5;

    public PtzMotionController(PtzAxes axes, PtzOptions options, ILogger<PtzMotionController>? logger = null)
    {
        _axes = axes;
        _options = options;
        _logger = logger ?? NullLogger<PtzMotionController>.Instance;
        foreach (var axis in axes.All) _rates[(int)axis.Kind] = MaxRate(axis, options);
    }

    public PtzAxes Axes => _axes;

    /// <summary>Jog speed as a fraction of the maximum rates in <see cref="PtzOptions"/>.</summary>
    public double JogSpeed
    {
        get => Volatile.Read(ref _jogSpeed);
        set => Volatile.Write(ref _jogSpeed, Math.Clamp(value, 0.01, 1));
    }

    public bool IsMoving
    {
        get
        {
            lock (_lock) return _running;
        }
    }

    /// <summary>Raised on a background thread after every tick that sent a position, and when motion stops.</summary>
    public event EventHandler? Moved;

    /// <summary>Axis rate at 100 % speed, in device units per second.</summary>
    internal static double MaxRate(PtzAxis axis, PtzOptions options) => axis.Kind switch
    {
        _ when axis.IsAngular => options.MaxPanTiltDegreesPerSecond * PtzAxis.ArcsecondsPerDegree,
        PtzAxisKind.Zoom => axis.Range * options.MaxZoomRangePerSecond,
        PtzAxisKind.Focus => axis.Range * options.MaxFocusRangePerSecond,
        _ => axis.Range * options.MaxZoomRangePerSecond,
    };

    /// <summary>
    /// Sets the jog inputs, each from -1 to 1 (positive pan is right, positive tilt is up, positive zoom is tele).
    /// Any non-zero input cancels a preset move.
    /// </summary>
    public void SetJog(double pan, double tilt, double zoom, double focus)
    {
        lock (_lock)
        {
            _jog[(int)PtzAxisKind.Pan] = Math.Clamp(pan, -1, 1);
            _jog[(int)PtzAxisKind.Tilt] = Math.Clamp(tilt, -1, 1);
            _jog[(int)PtzAxisKind.Zoom] = Math.Clamp(zoom, -1, 1);
            _jog[(int)PtzAxisKind.Focus] = Math.Clamp(focus, -1, 1);
            if (!IsJogging()) return;
            CancelMove();
            EnsureRunning();
        }
    }

    /// <summary>
    /// Moves the given axes to <paramref name="target"/> with an ease-in/out. <paramref name="speed"/> (0–1) scales
    /// the peak speed of the slowest axis; all axes arrive together.
    /// </summary>
    /// <returns>True when the target was reached, false when the move was interrupted.</returns>
    public Task<bool> RecallAsync(PtzPosition target, double speed)
    {
        var targets = new double?[AxisCount];
        foreach (var axis in _axes.All)
        {
            if (target[axis.Kind] is { } value) targets[(int)axis.Kind] = axis.Snap(value);
        }
        if (targets.All(t => t is null)) return Task.FromResult(false);

        lock (_lock)
        {
            CancelMove();
            Array.Clear(_jog);
            _move = new PtzMove(targets, Math.Clamp(speed, 0.01, 1));
            var done = _moveDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnsureRunning();
            return done.Task;
        }
    }

    /// <summary>Stops jogging and any preset move where the camera is now.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            Array.Clear(_jog);
            CancelMove();
        }
    }

    private bool IsJogging() => _jog.Any(j => j != 0);

    private void CancelMove()
    {
        _move = null;
        _moveDone?.TrySetResult(false);
        _moveDone = null;
    }

    /// <summary>Starts the loop, taking the model's latest values as the starting point. Call under the lock.</summary>
    private void EnsureRunning()
    {
        if (_running || _cts.IsCancellationRequested) return;
        _running = true;
        foreach (var axis in _axes.All)
        {
            _position[(int)axis.Kind] = _sent[(int)axis.Kind] = axis.Current;
            axis.Control.IsEditing = true; // refreshes would otherwise pull the lagging real position back in
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool>? arrived = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / Math.Max(_options.TickRate, 1)));
            var clock = Stopwatch.StartNew();
            var last = TimeSpan.Zero;
            var idle = 0.0;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = clock.Elapsed;
                var dt = Math.Min((now - last).TotalSeconds, MaxTickSeconds);
                last = now;

                List<(UvcControl Control, byte[] Payload)> writes;
                lock (_lock)
                {
                    var active = Step(dt, out arrived);
                    writes = CollectWrites();
                    idle = active || writes.Count > 0 ? 0 : idle + dt;
                    if (idle >= IdleSeconds)
                    {
                        Finish();
                        break;
                    }
                }

                foreach (var (control, payload) in writes)
                    await control.SendAsync(payload).ConfigureAwait(false);
                if (writes.Count > 0) Moved?.Invoke(this, EventArgs.Empty);
                // Only now has the final position reached the camera.
                arrived?.TrySetResult(true);
                arrived = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // The device went away (its executor is disposed) or the transport failed badly.
            _logger.LogWarning(e, "PTZ motion stopped");
        }
        finally
        {
            arrived?.TrySetResult(false);
            lock (_lock)
            {
                if (_running) Finish();
            }
        }
        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Advances jogging or the preset move by <paramref name="dt"/>. Returns false when nothing moves.
    /// <paramref name="arrived"/> is the finished move's completion, to be set once its last position is sent.
    /// </summary>
    private bool Step(double dt, out TaskCompletionSource<bool>? arrived)
    {
        arrived = null;
        if (_move is { } move)
        {
            if (!move.IsStarted) move.Begin(_position, _rates, _options.MinimumMoveDuration.TotalSeconds);
            if (move.Advance(dt, _position))
            {
                _move = null;
                arrived = _moveDone;
                _moveDone = null;
            }
            return true;
        }

        if (!IsJogging()) return false;
        var speed = JogSpeed;
        foreach (var axis in _axes.All)
        {
            var i = (int)axis.Kind;
            _position[i] = Jog(_position[i], _jog[i], _rates[i] * speed, dt, axis.Minimum, axis.Maximum);
        }
        return true;
    }

    internal static double Jog(double position, double input, double rate, double dt, double minimum, double maximum) =>
        Math.Clamp(position + input * rate * dt, minimum, maximum);

    /// <summary>Payloads for every control whose snapped position changed. Marks them as sent.</summary>
    private List<(UvcControl, byte[])> CollectWrites()
    {
        var writes = new List<(UvcControl, byte[])>();
        foreach (var group in _axes.All.GroupBy(a => a.Control))
        {
            var changed = false;
            var payload = group.Key.Current;
            foreach (var axis in group)
            {
                var i = (int)axis.Kind;
                var value = axis.Snap(_position[i]);
                if (value != _sent[i]) changed = true;
                _sent[i] = value;
                // Pan and tilt share one payload: always write both.
                payload = payload.WithInt(value, axis.Field.Offset, axis.Field.Size);
            }
            if (!changed) continue;

            if (group.Any(a => a.Kind == PtzAxisKind.Focus) && _axes.Autofocus is { } af && af.Current.Any(b => b != 0))
                writes.Add((af, new byte[af.Length]));
            writes.Add((group.Key, payload));
        }
        return writes;
    }

    /// <summary>Stops the loop's bookkeeping. Call under the lock.</summary>
    private void Finish()
    {
        _running = false;
        CancelMove();
        foreach (var axis in _axes.All) axis.Control.IsEditing = false;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _cts.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>An eased move from wherever the axes are when it starts to a target.</summary>
internal sealed class PtzMove(double?[] target, double speed)
{
    private double[] _start = [];

    public bool IsStarted { get; private set; }

    public double Duration { get; private set; }

    public double Elapsed { get; private set; }

    /// <summary>
    /// Fixes the start and duration. The duration makes the slowest axis peak at its rate × speed,
    /// so every axis arrives together and none exceeds its speed.
    /// </summary>
    public void Begin(double[] positions, double[] rates, double minimumDuration)
    {
        _start = (double[])positions.Clone();
        var longest = 0.0;
        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] is not { } t || rates[i] <= 0) continue;
            longest = Math.Max(longest, Math.Abs(t - _start[i]) * Easing.InOutCubicPeak / (rates[i] * speed));
        }
        Duration = longest > 0 ? Math.Max(longest, minimumDuration) : 0;
        IsStarted = true;
    }

    /// <summary>Advances by <paramref name="dt"/> seconds and writes the eased positions. Returns true on arrival.</summary>
    public bool Advance(double dt, double[] positions)
    {
        Elapsed += dt;
        var progress = Duration <= 0 ? 1 : Math.Min(Elapsed / Duration, 1);
        var eased = Easing.InOutCubic(progress);
        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] is { } t) positions[i] = progress >= 1 ? t : _start[i] + (t - _start[i]) * eased;
        }
        return progress >= 1;
    }
}
