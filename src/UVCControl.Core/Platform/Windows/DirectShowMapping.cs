using System.Runtime.Versioning;
using UVCControl.Core.Transport;
using CC = UVCControl.Core.Platform.Windows.DirectShow.CameraControl;
using PA = UVCControl.Core.Platform.Windows.DirectShow.VideoProcAmp;

namespace UVCControl.Core.Platform.Windows;

/// <summary>Emulates UVC requests for one selector on top of a DirectShow camera/proc-amp property.</summary>
[SupportedOSPlatform("windows")]
internal abstract class DirectShowMapping
{
    public abstract byte[] Get(WindowsUvcTransport t, UvcRequest request, int length);

    public abstract void Set(WindowsUvcTransport t, byte[] payload);

    protected static UvcException Unsupported() => new("Not available through DirectShow");

    protected static void Check(int hr)
    {
        if (hr < 0) throw new UvcException($"HRESULT 0x{hr:X8}");
    }

    public static readonly IReadOnlyDictionary<byte, DirectShowMapping> CameraTerminal = new Dictionary<byte, DirectShowMapping>
    {
        [0x01] = new ValueMapping(true, (int)CC.ScanMode),
        [0x02] = new AeModeMapping(),
        [0x03] = new ValueMapping(true, (int)CC.AutoExposurePriority),
        [0x04] = new ExposureMapping(),
        [0x05] = new RelativeMapping(true, (int)CC.ExposureRelative, hasSpeed: false),
        [0x06] = new ValueMapping(true, (int)CC.Focus),
        [0x07] = new RelativeMapping(true, (int)CC.FocusRelative),
        [0x08] = new AutoFlagMapping(true, (int)CC.Focus),
        [0x09] = new ValueMapping(true, (int)CC.Iris),
        [0x0A] = new RelativeMapping(true, (int)CC.IrisRelative, hasSpeed: false),
        [0x0B] = new ValueMapping(true, (int)CC.Zoom),
        [0x0C] = new RelativeMapping(true, (int)CC.ZoomRelative, speedOffset: 2),
        // DirectShow reports pan/tilt in degrees; UVC uses arc-seconds.
        [0x0D] = new ValueMapping(true, (0, 4, (int)CC.Pan, 3600), (4, 4, (int)CC.Tilt, 3600)),
        [0x0E] = new PanTiltRelativeMapping(),
        [0x0F] = new ValueMapping(true, (int)CC.Roll),
        [0x10] = new RelativeMapping(true, (int)CC.RollRelative),
        [0x11] = new ValueMapping(true, (int)CC.Privacy),
    };

    public static readonly IReadOnlyDictionary<byte, DirectShowMapping> ProcessingUnit = new Dictionary<byte, DirectShowMapping>
    {
        [0x01] = new ValueMapping(false, (int)PA.BacklightCompensation),
        [0x02] = new ValueMapping(false, (int)PA.Brightness),
        [0x03] = new ValueMapping(false, (int)PA.Contrast),
        [0x04] = new ValueMapping(false, (int)PA.Gain),
        [0x05] = new ValueMapping(false, (int)PA.PowerLineFrequency),
        [0x06] = new ValueMapping(false, (int)PA.Hue),
        [0x07] = new ValueMapping(false, (int)PA.Saturation),
        [0x08] = new ValueMapping(false, (int)PA.Sharpness),
        [0x09] = new ValueMapping(false, (int)PA.Gamma),
        [0x0A] = new ValueMapping(false, (int)PA.WhiteBalance),
        [0x0B] = new AutoFlagMapping(false, (int)PA.WhiteBalance),
        [0x0E] = new ValueMapping(false, (int)PA.DigitalMultiplier),
        [0x0F] = new ValueMapping(false, (int)PA.DigitalMultiplierLimit),
        [0x10] = new AutoFlagMapping(false, (int)PA.Hue),
    };

    protected static IDirectShowPropertySet Set(WindowsUvcTransport t, bool camera) =>
        (camera ? t.Camera : t.ProcAmp) ?? throw Unsupported();

    protected readonly record struct Range(int Min, int Max, int Step, int Default, int Caps)
    {
        public bool SupportsAuto => (Caps & DirectShow.FlagsAuto) != 0;
    }

    protected static Range GetRange(IDirectShowPropertySet set, int property)
    {
        Check(set.GetRange(property, out var min, out var max, out var step, out var def, out var caps));
        return new Range(min, max, step, def, caps);
    }

    /// <summary>Returns (value, isAuto).</summary>
    protected static (int Value, bool Auto) GetCurrent(IDirectShowPropertySet set, int property)
    {
        Check(set.Get(property, out var value, out var flags));
        return (value, (flags & DirectShow.FlagsAuto) != 0);
    }
}

/// <summary>Each UVC field is one DirectShow property, optionally scaled.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ValueMapping : DirectShowMapping
{
    private readonly bool _camera;
    private readonly (int Offset, int Size, int Property, int Scale)[] _fields;

    public ValueMapping(bool camera, int property) : this(camera, (0, 0, property, 1)) { }

    public ValueMapping(bool camera, params (int Offset, int Size, int Property, int Scale)[] fields)
    {
        _camera = camera;
        _fields = fields;
    }

    public override byte[] Get(WindowsUvcTransport t, UvcRequest request, int length)
    {
        var set = Set(t, _camera);
        if (request == UvcRequest.GetInfo)
        {
            var (_, auto) = GetCurrent(set, _fields[0].Property);
            var range = GetRange(set, _fields[0].Property);
            return [(byte)(0x03 | (auto && range.SupportsAuto ? 0x04 : 0))];
        }

        var buffer = new byte[length];
        foreach (var (offset, size, property, scale) in _fields)
        {
            var range = GetRange(set, property);
            long value = request switch
            {
                UvcRequest.GetCur => GetCurrent(set, property).Value,
                UvcRequest.GetMin => range.Min,
                UvcRequest.GetMax => range.Max,
                UvcRequest.GetRes => Math.Max(range.Step, 1),
                UvcRequest.GetDef => range.Default,
                _ => throw Unsupported(),
            };
            buffer.WriteInt(value * scale, offset, size == 0 ? length : size);
        }
        return buffer;
    }

    public override void Set(WindowsUvcTransport t, byte[] payload)
    {
        var set = Set(t, _camera);
        foreach (var (offset, size, property, scale) in _fields)
        {
            var value = payload.ReadInt(offset, size == 0 ? payload.Length : size, signed: true) / scale;
            Check(set.Set(property, (int)value, DirectShow.FlagsManual));
        }
    }
}

/// <summary>An "auto" toggle backed by a property's auto flag.</summary>
[SupportedOSPlatform("windows")]
internal sealed class AutoFlagMapping(bool camera, int property) : DirectShowMapping
{
    public override byte[] Get(WindowsUvcTransport t, UvcRequest request, int length)
    {
        var set = Set(t, camera);
        var range = GetRange(set, property);
        if (!range.SupportsAuto) throw Unsupported();
        return request switch
        {
            UvcRequest.GetInfo => [0x03],
            UvcRequest.GetCur => [(byte)(GetCurrent(set, property).Auto ? 1 : 0)],
            UvcRequest.GetMin => [0],
            UvcRequest.GetMax or UvcRequest.GetRes => [1],
            UvcRequest.GetDef => [1],
            _ => throw Unsupported(),
        };
    }

    public override void Set(WindowsUvcTransport t, byte[] payload)
    {
        var set = Set(t, camera);
        var (value, _) = GetCurrent(set, property);
        Check(set.Set(property, value, payload[0] != 0 ? DirectShow.FlagsAuto : DirectShow.FlagsManual));
    }
}

/// <summary>CT_AE_MODE from the exposure auto flag: Manual (0x01) or Aperture Priority (0x08).</summary>
[SupportedOSPlatform("windows")]
internal sealed class AeModeMapping : DirectShowMapping
{
    private const int Exposure = (int)CC.Exposure;

    public override byte[] Get(WindowsUvcTransport t, UvcRequest request, int length)
    {
        var set = Set(t, camera: true);
        var range = GetRange(set, Exposure);
        return request switch
        {
            UvcRequest.GetInfo => [0x03],
            UvcRequest.GetCur => [(byte)(GetCurrent(set, Exposure).Auto ? 0x08 : 0x01)],
            UvcRequest.GetRes => [(byte)(0x01 | (range.SupportsAuto ? 0x08 : 0))],
            UvcRequest.GetDef => [(byte)(range.SupportsAuto ? 0x08 : 0x01)],
            _ => throw Unsupported(),
        };
    }

    public override void Set(WindowsUvcTransport t, byte[] payload)
    {
        var set = Set(t, camera: true);
        var (value, _) = GetCurrent(set, Exposure);
        Check(set.Set(Exposure, value, payload[0] == 0x01 ? DirectShow.FlagsManual : DirectShow.FlagsAuto));
    }
}

/// <summary>Exposure time: DirectShow uses log2(seconds), UVC uses 100 µs units.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ExposureMapping : DirectShowMapping
{
    private const int Exposure = (int)CC.Exposure;

    private static long ToUvc(int log2Seconds) => Math.Max(1, (long)Math.Round(Math.Pow(2, log2Seconds) * 10_000));

    private static int FromUvc(long hundredMicroseconds) =>
        (int)Math.Round(Math.Log2(Math.Max(hundredMicroseconds, 1) / 10_000.0));

    public override byte[] Get(WindowsUvcTransport t, UvcRequest request, int length)
    {
        var set = Set(t, camera: true);
        var range = GetRange(set, Exposure);
        var current = GetCurrent(set, Exposure);
        var buffer = new byte[4];
        switch (request)
        {
            case UvcRequest.GetInfo: return [(byte)(0x03 | (current.Auto ? 0x04 : 0))];
            case UvcRequest.GetCur: buffer.WriteInt(ToUvc(current.Value), 0, 4); break;
            case UvcRequest.GetMin: buffer.WriteInt(ToUvc(range.Min), 0, 4); break;
            case UvcRequest.GetMax: buffer.WriteInt(ToUvc(range.Max), 0, 4); break;
            case UvcRequest.GetRes: buffer.WriteInt(1, 0, 4); break;
            case UvcRequest.GetDef: buffer.WriteInt(ToUvc(range.Default), 0, 4); break;
            default: throw Unsupported();
        }
        return buffer;
    }

    public override void Set(WindowsUvcTransport t, byte[] payload)
    {
        var set = Set(t, camera: true);
        var range = GetRange(set, Exposure);
        var value = Math.Clamp(FromUvc(payload.ReadInt(0, 4, signed: false)), range.Min, range.Max);
        Check(set.Set(Exposure, value, DirectShow.FlagsManual));
    }
}

/// <summary>A relative control whose DirectShow value is direction × speed.</summary>
[SupportedOSPlatform("windows")]
internal class RelativeMapping(bool camera, int property, bool hasSpeed = true, int speedOffset = 1) : DirectShowMapping
{
    public override byte[] Get(WindowsUvcTransport t, UvcRequest request, int length)
    {
        var range = GetRange(Set(t, camera), property);
        if (request == UvcRequest.GetInfo) return [0x03];
        var buffer = new byte[length];
        if (hasSpeed && speedOffset < length)
        {
            var maxSpeed = (byte)Math.Clamp(Math.Max(Math.Abs(range.Min), Math.Abs(range.Max)), 1, 255);
            buffer[speedOffset] = request switch
            {
                UvcRequest.GetMin or UvcRequest.GetRes => 1,
                UvcRequest.GetMax or UvcRequest.GetDef => maxSpeed,
                _ => 0,
            };
        }
        return buffer;
    }

    protected int SpeedOffset => hasSpeed ? speedOffset : -1;

    public override void Set(WindowsUvcTransport t, byte[] payload) => Move(t, payload, 0, SpeedOffset);

    protected void Move(WindowsUvcTransport t, byte[] payload, int directionOffset, int speed)
    {
        var set = Set(t, camera);
        var range = GetRange(set, property);
        var direction = Math.Sign((sbyte)payload[directionOffset]);
        var magnitude = speed >= 0 && speed < payload.Length ? Math.Max((int)payload[speed], 1) : 1;
        Check(set.Set(property, Math.Clamp(direction * magnitude, range.Min, range.Max), DirectShow.FlagsManual));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PanTiltRelativeMapping : DirectShowMapping
{
    private readonly RelativeMapping _pan = new PanTiltAxis((int)CC.PanRelative, 0, 1);
    private readonly RelativeMapping _tilt = new PanTiltAxis((int)CC.TiltRelative, 2, 3);

    public override byte[] Get(WindowsUvcTransport t, UvcRequest request, int length)
    {
        var pan = _pan.Get(t, request, length);
        if (request == UvcRequest.GetInfo) return pan;
        byte[] tilt;
        try
        {
            tilt = _tilt.Get(t, request, length);
        }
        catch (UvcException)
        {
            return pan;
        }
        return pan.Zip(tilt, (a, b) => (byte)(a | b)).ToArray();
    }

    public override void Set(WindowsUvcTransport t, byte[] payload)
    {
        _pan.Set(t, payload);
        _tilt.Set(t, payload);
    }

    private sealed class PanTiltAxis(int property, int directionOffset, int speedOffset)
        : RelativeMapping(true, property, true, speedOffset)
    {
        public override void Set(WindowsUvcTransport t, byte[] payload) => Move(t, payload, directionOffset, SpeedOffset);
    }
}
