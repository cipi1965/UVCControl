using UVCControl.Core.Controls;
using UVCControl.Core.Devices;

namespace UVCControl.Core.Ptz;

public enum PtzAxisKind
{
    Pan,
    Tilt,
    Zoom,
    Focus,
}

/// <summary>A position for some axes; null leaves that axis where it is.</summary>
public readonly record struct PtzPosition(long? Pan = null, long? Tilt = null, long? Zoom = null, long? Focus = null)
{
    public long? this[PtzAxisKind kind] => kind switch
    {
        PtzAxisKind.Pan => Pan,
        PtzAxisKind.Tilt => Tilt,
        PtzAxisKind.Zoom => Zoom,
        _ => Focus,
    };
}

/// <summary>One field of an absolute control that the PTZ controller drives.</summary>
public sealed class PtzAxis
{
    public const double ArcsecondsPerDegree = 3600;

    internal PtzAxis(PtzAxisKind kind, UvcControl control, ControlField field, bool snapToResolution)
    {
        Kind = kind;
        Control = control;
        Field = field;
        var a = control.MinValue(field) ?? 0;
        var b = control.MaxValue(field) ?? 0;
        Minimum = Math.Min(a, b);
        Maximum = Math.Max(a, b);
        Resolution = Math.Max(control.ResValue(field) ?? 1, 1);
        Step = snapToResolution ? Resolution : 1;
    }

    public PtzAxisKind Kind { get; }
    public UvcControl Control { get; }
    public ControlField Field { get; }
    public long Minimum { get; }
    public long Maximum { get; }
    /// <summary>The step the camera reports with GET_RES.</summary>
    public long Resolution { get; }

    /// <summary>The step positions are rounded to: <see cref="Resolution"/>, or 1 when finer positions are allowed.</summary>
    public long Step { get; }
    public long Range => Maximum - Minimum;

    /// <summary>Pan and tilt are in arcseconds, so their speed is set in degrees per second.</summary>
    public bool IsAngular => Field.Unit == DisplayUnit.Arcseconds;

    /// <summary>The last value read from, or written to, the device.</summary>
    public long Current => Control.Value(Field);

    public long? Default => Control.DefValue(Field) is { } d ? Snap(d) : null;

    /// <summary>Rounds to <see cref="Step"/>, anchored at the minimum, and clamps to the range.</summary>
    public long Snap(double value) =>
        Math.Clamp(Minimum + (long)Math.Round((value - Minimum) / Step) * Step, Minimum, Maximum);

    /// <summary>Position from 0 (minimum) to 1 (maximum), for displays.</summary>
    public double Fraction(long value) => Range == 0 ? 0 : (double)(value - Minimum) / Range;
}
