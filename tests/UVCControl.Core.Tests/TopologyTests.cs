using UVCControl.Core.Descriptors;

namespace UVCControl.Core.Tests;

public class TopologyTests
{
    /// <summary>Config descriptor with a VideoControl interface: header, camera terminal, processing unit, extension unit.</summary>
    internal static byte[] SampleDescriptor()
    {
        var bytes = new List<byte>();
        bytes.AddRange([0x09, 0x02, 0x00, 0x00, 0x02, 0x01, 0x00, 0x80, 0xFA]);            // CONFIGURATION
        bytes.AddRange([0x09, 0x04, 0x00, 0x00, 0x01, 0x0E, 0x01, 0x00, 0x00]);            // INTERFACE 0: VC
        bytes.AddRange([0x0D, 0x24, 0x01, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01]); // VC_HEADER 1.10
        bytes.AddRange([0x12, 0x24, 0x02, 0x01, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x0A, 0x02, 0x00]); // CT id 1: AE mode, exposure, zoom
        bytes.AddRange([0x0B, 0x24, 0x05, 0x02, 0x01, 0x00, 0x00, 0x02, 0x7F, 0x15, 0x00]); // PU id 2
        byte[] guid = [0xA2, 0x9E, 0x76, 0x41, 0xDE, 0x04, 0x47, 0xE3, 0x8B, 0x2B, 0xF4, 0x34, 0x1A, 0xFF, 0x00, 0x3B];
        bytes.AddRange([0x1A, 0x24, 0x06, 0x06]);                                            // XU id 6
        bytes.AddRange(guid);
        bytes.AddRange([0x02, 0x01, 0x02, 0x01, 0x03, 0x00]);                                 // 2 controls, 1 pin, bmControls 0x03
        bytes.AddRange([0x09, 0x04, 0x01, 0x00, 0x00, 0x0E, 0x02, 0x00, 0x00]);            // INTERFACE 1: VS
        bytes.AddRange([0x05, 0x24, 0x02, 0x09, 0x09]);                                      // ignored CS descriptor
        return bytes.ToArray();
    }

    [Fact]
    public void ParsesVideoControlUnits()
    {
        var topology = UvcTopology.Parse(SampleDescriptor());

        Assert.Equal("1.10", topology.VersionString);
        Assert.Equal(0, topology.InterfaceNumber);

        var ct = Assert.Single(topology.CameraTerminals);
        Assert.Equal(1, ct.Id);
        Assert.Equal([0x0A, 0x02, 0x00], ct.BmControls);

        var pu = Assert.Single(topology.ProcessingUnits);
        Assert.Equal(2, pu.Id);
        Assert.Equal([0x7F, 0x15], pu.BmControls);

        var xu = Assert.Single(topology.ExtensionUnits);
        Assert.Equal(6, xu.Id);
        Assert.Equal(new Guid("41769EA2-04DE-E347-8B2B-F4341AFF003B"), xu.Guid);
        Assert.Equal(2, xu.NumControls);
        Assert.Equal([0x03], xu.BmControls);
    }

    [Fact]
    public void SkipsLeadingDeviceDescriptor()
    {
        // Linux sysfs "descriptors" starts with the 18-byte device descriptor.
        byte[] device = [0x12, 0x01, 0x00, 0x02, 0xEF, 0x02, 0x01, 0x40, 0xA3, 0x2C, 0x23, 0x00, 0x00, 0x01, 0x01, 0x02, 0x03, 0x01];
        var topology = UvcTopology.Parse([.. device, .. SampleDescriptor()]);
        Assert.Single(topology.CameraTerminals);
    }

    [Fact]
    public void ToleratesTruncatedInput()
    {
        var bytes = SampleDescriptor();
        for (var length = 0; length < bytes.Length; length++)
            _ = UvcTopology.Parse(bytes.AsSpan(0, length));
    }
}
