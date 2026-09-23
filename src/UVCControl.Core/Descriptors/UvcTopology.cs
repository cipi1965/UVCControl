namespace UVCControl.Core.Descriptors;

/// <summary>
/// The parts of a UVC VideoControl interface that expose controls, parsed from the configuration descriptor.
/// </summary>
public sealed class UvcTopology
{
    public sealed record Terminal(byte Id, ushort TerminalType, byte[] BmControls);

    public sealed record ProcessingUnit(byte Id, byte[] BmControls);

    public sealed record ExtensionUnit(byte Id, Guid Guid, byte NumControls, byte[] BmControls);

    public ushort UvcVersion { get; init; }
    public byte InterfaceNumber { get; init; }
    public List<Terminal> CameraTerminals { get; } = [];
    public List<ProcessingUnit> ProcessingUnits { get; } = [];
    public List<ExtensionUnit> ExtensionUnits { get; } = [];

    public string VersionString => $"{UvcVersion >> 8:x}.{UvcVersion & 0xFF:x2}";

    public static readonly UvcTopology Empty = new();

    /// <summary>Parses the first VideoControl interface found in a configuration descriptor.</summary>
    /// <remarks>Leading non-interface descriptors (e.g. a device descriptor from Linux sysfs) are skipped.</remarks>
    public static UvcTopology Parse(ReadOnlySpan<byte> d)
    {
        ushort version = 0;
        byte interfaceNumber = 0;
        var cameraTerminals = new List<Terminal>();
        var processingUnits = new List<ProcessingUnit>();
        var extensionUnits = new List<ExtensionUnit>();
        var inVideoControl = false;
        var seenVideoControl = false;

        var i = 0;
        while (i + 2 <= d.Length)
        {
            int length = d[i];
            if (length < 2 || i + length > d.Length) break;
            var desc = d.Slice(i, length);
            i += length;

            switch (desc[1])
            {
                case 0x04 when length >= 9: // INTERFACE
                    var isVideoControl = desc[5] == 0x0E && desc[6] == 0x01 && desc[3] == 0;
                    inVideoControl = isVideoControl && !seenVideoControl;
                    if (inVideoControl)
                    {
                        seenVideoControl = true;
                        interfaceNumber = desc[2];
                    }
                    break;
                case 0x24 when inVideoControl && length >= 3: // CS_INTERFACE
                    ParseVideoControl(desc, ref version, cameraTerminals, processingUnits, extensionUnits);
                    break;
            }
        }

        var topology = new UvcTopology { UvcVersion = version, InterfaceNumber = interfaceNumber };
        topology.CameraTerminals.AddRange(cameraTerminals);
        topology.ProcessingUnits.AddRange(processingUnits);
        topology.ExtensionUnits.AddRange(extensionUnits);
        return topology;
    }

    private static void ParseVideoControl(ReadOnlySpan<byte> desc, ref ushort version, List<Terminal> cameraTerminals,
                                          List<ProcessingUnit> processingUnits, List<ExtensionUnit> extensionUnits)
    {
        var length = desc.Length;
        switch (desc[2])
        {
            case 0x01 when length >= 5: // VC_HEADER
                version = (ushort)(desc[3] | desc[4] << 8);
                break;
            case 0x02 when length >= 8: // VC_INPUT_TERMINAL
                var type = (ushort)(desc[4] | desc[5] << 8);
                if (type != 0x0201 || length < 15) return; // ITT_CAMERA
                int ctSize = desc[14];
                cameraTerminals.Add(new Terminal(desc[3], type, desc[15..Math.Min(length, 15 + ctSize)].ToArray()));
                break;
            case 0x05 when length >= 8: // VC_PROCESSING_UNIT
                int puSize = desc[7];
                processingUnits.Add(new ProcessingUnit(desc[3], desc[8..Math.Min(length, 8 + puSize)].ToArray()));
                break;
            case 0x06 when length >= 24: // VC_EXTENSION_UNIT
                int pins = desc[21];
                var sizeIndex = 22 + pins;
                if (sizeIndex >= length) return;
                int xuSize = desc[sizeIndex];
                var controls = desc[(sizeIndex + 1)..Math.Min(length, sizeIndex + 1 + xuSize)].ToArray();
                // USB GUIDs use the Microsoft mixed-endian layout, which is exactly what System.Guid expects.
                extensionUnits.Add(new ExtensionUnit(desc[3], new Guid(desc[4..20]), desc[20], controls));
                break;
        }
    }
}
