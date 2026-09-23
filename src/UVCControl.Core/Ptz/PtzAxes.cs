using UVCControl.Core.Devices;

namespace UVCControl.Core.Ptz;

/// <summary>The absolute Camera Terminal controls a camera offers for PTZ.</summary>
public sealed class PtzAxes
{
    private const byte PanTiltAbsolute = 0x0D;
    private const byte ZoomAbsolute = 0x0B;
    private const byte FocusAbsolute = 0x06;
    private const byte AutofocusSelector = 0x08;

    private readonly PtzAxis?[] _axes = new PtzAxis?[4];
    private bool _snapToResolution;

    public static readonly PtzAxes None = new();

    public PtzAxis? Pan => this[PtzAxisKind.Pan];
    public PtzAxis? Tilt => this[PtzAxisKind.Tilt];
    public PtzAxis? Zoom => this[PtzAxisKind.Zoom];
    public PtzAxis? Focus => this[PtzAxisKind.Focus];

    /// <summary>Turned off before focus is driven by hand or by a preset.</summary>
    public UvcControl? Autofocus { get; private set; }

    public PtzAxis? this[PtzAxisKind kind] => _axes[(int)kind];

    public IEnumerable<PtzAxis> All => _axes.OfType<PtzAxis>();

    public bool IsEmpty => !All.Any();

    /// <param name="snapToResolution">Round positions to the camera's GET_RES step instead of single units.</param>
    public static PtzAxes From(UvcDevice device, bool snapToResolution = false)
    {
        var axes = new PtzAxes { _snapToResolution = snapToResolution };
        var controls = device.Sections.FirstOrDefault(s => s.Kind == UnitKind.CameraTerminal)?.Controls ?? [];
        UvcControl? Find(byte selector) => controls.FirstOrDefault(c => c.Definition.Selector == selector && c.SupportsSet);

        if (Find(PanTiltAbsolute) is { } panTilt)
        {
            axes.Add(PtzAxisKind.Pan, panTilt, 0);
            axes.Add(PtzAxisKind.Tilt, panTilt, 1);
        }
        if (Find(ZoomAbsolute) is { } zoom) axes.Add(PtzAxisKind.Zoom, zoom, 0);
        if (Find(FocusAbsolute) is { } focus) axes.Add(PtzAxisKind.Focus, focus, 0);
        axes.Autofocus = Find(AutofocusSelector);
        return axes;
    }

    /// <summary>Where the camera is, as far as the model knows.</summary>
    public PtzPosition CurrentPosition() => new(Pan?.Current, Tilt?.Current, Zoom?.Current, Focus?.Current);

    /// <summary>The device's default for every axis (usually centred and zoomed out).</summary>
    public PtzPosition HomePosition() => new(Pan?.Default, Tilt?.Default, Zoom?.Default, null);

    private void Add(PtzAxisKind kind, UvcControl control, int fieldIndex)
    {
        var axis = new PtzAxis(kind, control, control.Definition.Fields[fieldIndex], _snapToResolution);
        // Without a usable range there's nothing to move across.
        if (axis.Range > 0) _axes[(int)kind] = axis;
    }
}
