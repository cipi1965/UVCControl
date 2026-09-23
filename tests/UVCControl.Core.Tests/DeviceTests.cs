using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UVCControl.Core.Descriptors;
using UVCControl.Core.Devices;
using UVCControl.Core.Transport;

namespace UVCControl.Core.Tests;

/// <summary>A camera that answers Zoom (CT 0x0B), Autofocus (CT 0x08, unadvertised) and one XU control.</summary>
internal sealed class FakeCamera : IUvcBackend, IUvcTransport
{
    public readonly Dictionary<(byte Unit, byte Selector), byte[]> Current = new()
    {
        [(1, 0x0B)] = [0x64, 0x00],
        [(1, 0x08)] = [0x01],
        [(6, 0x01)] = [0x11, 0x22, 0x33],
    };

    public List<(byte Unit, byte Selector, byte[] Data)> Writes { get; } = [];

    public string Name => "Fake";
    public IReadOnlyList<UvcDeviceInfo> Enumerate() => [new("fake", "Fake Cam", 0x2CA3, 0x0023, "test")];
    public IUvcTransport Open(UvcDeviceInfo device) => this;
    public UvcTopology ReadTopology() => UvcTopology.Parse(TopologyTests.SampleDescriptor());

    public byte[] Get(UvcRequest request, byte unitId, byte selector, int length)
    {
        if (!Current.TryGetValue((unitId, selector), out var cur)) throw new UvcException("Stalled");
        return request switch
        {
            UvcRequest.GetInfo => [0x03],
            UvcRequest.GetLen => [(byte)cur.Length, 0],
            UvcRequest.GetCur => cur,
            UvcRequest.GetMin => new byte[cur.Length].WithInt(100, 0, Math.Min(2, cur.Length)),
            UvcRequest.GetMax => new byte[cur.Length].WithInt(400, 0, Math.Min(2, cur.Length)),
            UvcRequest.GetRes => new byte[cur.Length].WithInt(1, 0, 1),
            UvcRequest.GetDef => new byte[cur.Length].WithInt(100, 0, Math.Min(2, cur.Length)),
            _ => throw new UvcException("Unsupported"),
        };
    }

    public void Set(byte unitId, byte selector, byte[] data)
    {
        Writes.Add((unitId, selector, data));
        Current[(unitId, selector)] = data;
    }

    public void Dispose() { }
}

public class DeviceTests
{
    private static (IUvcDeviceManager Manager, FakeCamera Camera, IHost Host) Build()
    {
        var camera = new FakeCamera();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IUvcBackend>(camera);
        builder.Services.AddUvcControl();
        builder.Services.Configure<UvcOptions>(o => o.RefreshAfterWrite = TimeSpan.FromMilliseconds(10));
        var host = builder.Build();
        return (host.Services.GetRequiredService<IUvcDeviceManager>(), camera, host);
    }

    [Fact]
    public async Task DiscoversAnsweredControlsIncludingUnadvertised()
    {
        var (manager, _, host) = Build();
        using var _ = host;
        var device = Assert.Single(await manager.ReloadAsync());
        await device.DiscoverAsync();

        Assert.Null(device.Error);
        Assert.Equal(["Camera Terminal", "Processing Unit", "Extension Unit"], device.Sections.Select(s => s.Title));

        var ct = device.Sections[0].Controls;
        Assert.Equal(["Autofocus", "Zoom"], ct.Select(c => c.Definition.Name));
        Assert.False(ct[0].Advertised);  // bit 17 not set
        Assert.True(ct[1].Advertised);   // bit 9 set

        Assert.Empty(device.Sections[1].Controls);

        var xu = Assert.Single(device.Sections[2].Controls);
        Assert.Equal(3, xu.Length);
        Assert.Equal([0x11, 0x22, 0x33], xu.Current);
    }

    [Fact]
    public async Task SetWritesFieldAndRefreshes()
    {
        var (manager, camera, host) = Build();
        using var _ = host;
        var device = (await manager.ReloadAsync())[0];
        await device.DiscoverAsync();
        var zoom = device.AllControls.Single(c => c.Definition.Name == "Zoom");

        var refreshed = new TaskCompletionSource();
        device.Refreshed += (_, _) => refreshed.TrySetResult();
        await zoom.SetAsync(zoom.Definition.Fields[0], 250);

        var write = Assert.Single(camera.Writes);
        Assert.Equal((1, 0x0B), (write.Unit, write.Selector));
        Assert.Equal([0xFA, 0x00], write.Data);
        Assert.Equal(250, zoom.Value(zoom.Definition.Fields[0]));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ResetAllSkipsRawControls()
    {
        var (manager, camera, host) = Build();
        using var _ = host;
        var device = (await manager.ReloadAsync())[0];
        await device.DiscoverAsync();

        await device.ResetAllToDefaultsAsync();

        Assert.DoesNotContain(camera.Writes, w => w.Unit == 6);
        Assert.Contains(camera.Writes, w => w is { Unit: 1, Selector: 0x0B });
    }

    [Fact]
    public async Task ReloadKeepsExistingInstances()
    {
        var (manager, _, host) = Build();
        using var _ = host;
        var first = (await manager.ReloadAsync())[0];
        var second = (await manager.ReloadAsync())[0];
        Assert.Same(first, second);
    }
}
