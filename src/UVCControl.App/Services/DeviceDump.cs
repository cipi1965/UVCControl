using UVCControl.Core;
using UVCControl.Core.Devices;

namespace UVCControl.App.Services;

/// <summary>Prints every discovered control of every camera; used by <c>--dump</c>.</summary>
internal static class DeviceDump
{
    public static async Task<int> RunAsync(IUvcDeviceManager manager, TextWriter output)
    {
        output.WriteLine($"Backend: {manager.BackendName}");
        foreach (var device in await manager.ReloadAsync())
        {
            await device.DiscoverAsync();
            output.WriteLine($"{device.Name} [{device.VidPidString}] UVC {device.Topology.VersionString} @ {device.Info.Location}");
            if (device.Error is { } error) output.WriteLine($"  error: {error}");
            foreach (var section in device.Sections)
            {
                output.WriteLine($"  {section.Title} #{section.UnitId}");
                if (section.ExtensionGuid is { } guid) output.WriteLine($"    GUID {guid.ToString().ToUpperInvariant()}");
                foreach (var c in section.Controls)
                {
                    var flags = $"info=0x{c.Info:X2}" + (c.Advertised ? "" : " (not advertised)");
                    output.WriteLine($"    [{c.Definition.Selector}] {c.Definition.Name} {flags}");
                    output.WriteLine($"        cur={c.Current.ToHexString()}");
                    if (c.Minimum is { } min) output.WriteLine($"        min={min.ToHexString()}");
                    if (c.Maximum is { } max) output.WriteLine($"        max={max.ToHexString()}");
                    if (c.Resolution is { } res) output.WriteLine($"        res={res.ToHexString()}");
                    if (c.DefaultValue is { } def) output.WriteLine($"        def={def.ToHexString()}");
                }
            }
        }
        return 0;
    }
}
