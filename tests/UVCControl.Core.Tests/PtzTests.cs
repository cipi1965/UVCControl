using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UVCControl.Core.Descriptors;
using UVCControl.Core.Devices;
using UVCControl.Core.Ptz;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Tests;

/// <summary>A PTZ camera: Pan/Tilt ±180° in 1° steps, Zoom 100–400, Focus 0–255 and Autofocus.</summary>
internal sealed class FakePtzCamera : IUvcBackend, IUvcTransport
{
    private const int Degree = 3600;

    private readonly Dictionary<byte, (byte[] Min, byte[] Max, byte[] Res, byte[] Def)> _ranges = new()
    {
        [0x0D] = (PanTilt(-180 * Degree, -90 * Degree), PanTilt(180 * Degree, 90 * Degree), PanTilt(Degree, Degree), PanTilt(0, 0)),
        [0x0B] = (Int16(100), Int16(400), Int16(1), Int16(100)),
        [0x06] = (Int16(0), Int16(255), Int16(1), Int16(0)),
        [0x08] = ([0], [1], [1], [1]),
    };

    public readonly Dictionary<byte, byte[]> Current = new()
    {
        [0x0D] = PanTilt(0, 0),
        [0x0B] = Int16(100),
        [0x06] = Int16(0),
        [0x08] = [1],
    };

    public List<(byte Selector, byte[] Data)> Writes { get; } = [];

    public static byte[] PanTilt(int pan, int tilt) => new byte[8].WithInt(pan, 0, 4).WithInt(tilt, 4, 4);

    private static byte[] Int16(int value) => new byte[2].WithInt(value, 0, 2);

    public string Name => "Fake PTZ";
    public IReadOnlyList<UvcDeviceInfo> Enumerate() => [new("ptz", "Fake PTZ", 0x1234, 0x5678, "test")];
    public IUvcTransport Open(UvcDeviceInfo device) => this;
    public UvcTopology ReadTopology() => UvcTopology.Parse(TopologyTests.SampleDescriptor());

    public byte[] Get(UvcRequest request, byte unitId, byte selector, int length)
    {
        lock (Writes)
        {
            if (unitId != 1 || !Current.TryGetValue(selector, out var cur)) throw new UvcException("Stalled");
            var range = _ranges[selector];
            return request switch
            {
                UvcRequest.GetInfo => [0x03],
                UvcRequest.GetCur => cur,
                UvcRequest.GetMin => range.Min,
                UvcRequest.GetMax => range.Max,
                UvcRequest.GetRes => range.Res,
                UvcRequest.GetDef => range.Def,
                _ => throw new UvcException("Unsupported"),
            };
        }
    }

    public void Set(byte unitId, byte selector, byte[] data)
    {
        lock (Writes)
        {
            Writes.Add((selector, data));
            Current[selector] = data;
        }
    }

    public List<byte[]> WritesTo(byte selector)
    {
        lock (Writes) return Writes.Where(w => w.Selector == selector).Select(w => w.Data).ToList();
    }

    public void Dispose() { }
}

public class PtzTests
{
    private const int Degree = 3600;

    private static readonly PtzOptions FastOptions = new()
    {
        TickRate = 100,
        MaxPanTiltDegreesPerSecond = 360,
        MaxZoomRangePerSecond = 2,
        MaxFocusRangePerSecond = 2,
        MinimumMoveDuration = TimeSpan.FromMilliseconds(100),
    };

    private static async Task<(PtzAxes Axes, FakePtzCamera Camera, IHost Host)> BuildAsync(bool snapToResolution = false)
    {
        var camera = new FakePtzCamera();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IUvcBackend>(camera);
        builder.Services.AddUvcControl();
        builder.Services.Configure<UvcOptions>(o => o.RefreshAfterWrite = TimeSpan.FromMilliseconds(10));
        var host = builder.Build();
        var device = Assert.Single(await host.Services.GetRequiredService<IUvcDeviceManager>().ReloadAsync());
        await device.DiscoverAsync();
        return (PtzAxes.From(device, snapToResolution), camera, host);
    }

    [Fact]
    public void EasingStartsEndsAndIsSymmetric()
    {
        Assert.Equal(0, Easing.InOutCubic(0));
        Assert.Equal(1, Easing.InOutCubic(1));
        Assert.Equal(0.5, Easing.InOutCubic(0.5), 9);
        foreach (var t in new[] { 0.1, 0.25, 0.4 })
            Assert.Equal(1 - Easing.InOutCubic(t), Easing.InOutCubic(1 - t), 9);
        // Slow start: the first 10 % of the time covers much less than 10 % of the way.
        Assert.True(Easing.InOutCubic(0.1) < 0.01);
    }

    [Fact]
    public async Task FindsAxesAndSnapsToResolution()
    {
        var (axes, _, host) = await BuildAsync(snapToResolution: true);
        using var _ = host;

        Assert.NotNull(axes.Pan);
        Assert.NotNull(axes.Tilt);
        Assert.NotNull(axes.Zoom);
        Assert.NotNull(axes.Focus);
        Assert.NotNull(axes.Autofocus);
        Assert.Same(axes.Pan!.Control, axes.Tilt!.Control);
        Assert.True(axes.Pan.IsAngular);

        Assert.Equal(10 * Degree, axes.Pan.Snap(10.4 * Degree));
        Assert.Equal(11 * Degree, axes.Pan.Snap(10.6 * Degree));
        Assert.Equal(180 * Degree, axes.Pan.Snap(500 * Degree));
        Assert.Equal(-90 * Degree, axes.Tilt.Snap(-100 * Degree));
    }

    [Fact]
    public async Task KeepsSubStepPositionsByDefault()
    {
        var (axes, _, host) = await BuildAsync();
        using var _ = host;

        // The camera reports 1° steps, but positions in between must survive so slow moves stay smooth.
        Assert.Equal(Degree, axes.Pan!.Resolution);
        Assert.Equal(1, axes.Pan.Step);
        Assert.Equal(37440, axes.Pan.Snap(10.4 * Degree));
        Assert.Equal(180 * Degree, axes.Pan.Snap(500 * Degree));
    }

    [Fact]
    public async Task SlowRecallSendsSubDegreeSteps()
    {
        var (axes, camera, host) = await BuildAsync();
        using var _ = host;
        await using var ptz = new PtzMotionController(axes, FastOptions);

        await ptz.RecallAsync(new PtzPosition(Pan: 2 * Degree), 0.02).WaitAsync(TimeSpan.FromSeconds(5));

        var pans = camera.WritesTo(0x0D).Select(w => w.ReadInt(0, 4, signed: true)).ToList();
        Assert.Equal(2 * Degree, pans[^1]);
        Assert.Contains(pans, p => p % Degree != 0);
    }

    [Fact]
    public void MoveArrivesTogetherAndScalesWithSpeed()
    {
        double[] rates = [10, 10, 100, 0];
        double?[] target = [100, 20, 300, null];

        var slow = new PtzMove(target, 0.5);
        slow.Begin([0, 0, 100, 7], rates, 0);
        var fast = new PtzMove(target, 1);
        fast.Begin([0, 0, 100, 7], rates, 0);
        // Pan is the longest: 100 units at 10/s peak → 15 s at full speed (1.5× peak for the cubic).
        Assert.Equal(15, fast.Duration, 9);
        Assert.Equal(2 * fast.Duration, slow.Duration, 9);

        var positions = new double[] { 0, 0, 100, 7 };
        var previous = (double[])positions.Clone();
        var steps = new List<double>();
        var arrived = false;
        while (!arrived)
        {
            arrived = fast.Advance(0.5, positions);
            steps.Add(positions[0] - previous[0]);
            Assert.True(positions[0] >= previous[0] && positions[1] >= previous[1] && positions[2] >= previous[2]);
            previous = (double[])positions.Clone();
        }

        Assert.Equal([100, 20, 300, 7], positions);
        // Eased: small steps at both ends, bigger ones in the middle.
        Assert.True(steps[0] < steps[steps.Count / 2]);
        Assert.True(steps[^1] < steps[steps.Count / 2]);
    }

    [Fact]
    public void ShortMovesUseMinimumDuration()
    {
        var move = new PtzMove([1, null, null, null], 1);
        move.Begin([0, 0, 0, 0], [1000, 0, 0, 0], 0.25);
        Assert.Equal(0.25, move.Duration);
    }

    [Fact]
    public void JogClampsToRange()
    {
        Assert.Equal(15, PtzMotionController.Jog(10, 0.5, 20, 0.5, 0, 100));
        Assert.Equal(0, PtzMotionController.Jog(10, -1, 100, 1, 0, 100));
        Assert.Equal(100, PtzMotionController.Jog(90, 1, 100, 1, 0, 100));
    }

    [Fact]
    public async Task RecallStreamsEasedPositionsAndLeavesOtherAxes()
    {
        var (axes, camera, host) = await BuildAsync();
        using var _ = host;
        await using var ptz = new PtzMotionController(axes, FastOptions);

        Assert.True(await ptz.RecallAsync(new PtzPosition(Pan: 90 * Degree, Tilt: -30 * Degree), 1)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        var writes = camera.WritesTo(0x0D);
        Assert.True(writes.Count > 3);
        Assert.Equal(FakePtzCamera.PanTilt(90 * Degree, -30 * Degree), writes[^1]);
        var pans = writes.Select(w => w.ReadInt(0, 4, signed: true)).ToList();
        Assert.Equal(pans.Order(), pans);
        Assert.Empty(camera.WritesTo(0x0B)); // zoom wasn't part of the preset
    }

    [Fact]
    public async Task JoggingMovesUntilStoppedThenReleasesTheAxes()
    {
        var (axes, camera, host) = await BuildAsync();
        using var _ = host;
        await using var ptz = new PtzMotionController(axes, FastOptions) { JogSpeed = 1 };

        ptz.SetJog(1, 0, 1, 0);
        Assert.True(axes.Pan!.Control.IsEditing);
        await Task.Delay(150);
        ptz.Stop();
        while (ptz.IsMoving) await Task.Delay(10);

        Assert.True(axes.Pan.Current > 0);
        Assert.True(axes.Zoom!.Current > 100);
        Assert.Equal(0, camera.Current[0x0D].ReadInt(4, 4, signed: true)); // tilt untouched
        Assert.False(axes.Pan.Control.IsEditing);
    }

    [Fact]
    public async Task JoggingCancelsRecall()
    {
        var (axes, _, host) = await BuildAsync();
        using var _ = host;
        await using var ptz = new PtzMotionController(axes, FastOptions);

        var recall = ptz.RecallAsync(new PtzPosition(Pan: 180 * Degree), 0.05);
        ptz.SetJog(-1, 0, 0, 0);
        Assert.False(await recall.WaitAsync(TimeSpan.FromSeconds(5)));
        ptz.Stop();
    }

    [Fact]
    public async Task RecallingFocusTurnsAutofocusOff()
    {
        var (axes, camera, host) = await BuildAsync();
        using var _ = host;
        await using var ptz = new PtzMotionController(axes, FastOptions);

        await ptz.RecallAsync(new PtzPosition(Focus: 200), 1).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0], camera.WritesTo(0x08).First());
        Assert.Equal(200, camera.Current[0x06].ReadInt(0, 2, signed: false));
    }
}
