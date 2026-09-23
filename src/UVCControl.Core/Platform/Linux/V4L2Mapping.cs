using System.Runtime.Versioning;
using UVCControl.Core.Transport;
using static UVCControl.Core.Platform.Linux.V4L2;

namespace UVCControl.Core.Platform.Linux;

/// <summary>
/// Emulates UVC requests for one Camera Terminal / Processing Unit selector on top of the
/// V4L2 control(s) uvcvideo maps it to (drivers/media/usb/uvc/uvc_ctrl.c).
/// </summary>
[SupportedOSPlatform("linux")]
internal abstract class V4L2Mapping
{
    public abstract byte[] Get(LinuxUvcTransport t, UvcRequest request, int length);

    public abstract void Set(LinuxUvcTransport t, byte[] payload);

    /// <summary>Synthesizes a GET_INFO byte from V4L2 control flags.</summary>
    protected static byte Info(V4L2QueryCtrl q, bool forceGet = false)
    {
        var info = 0x01;
        if ((q.Flags & V4L2_CTRL_FLAG_READ_ONLY) == 0) info |= 0x02;
        if ((q.Flags & V4L2_CTRL_FLAG_INACTIVE) != 0) info |= 0x04;
        if ((q.Flags & V4L2_CTRL_FLAG_VOLATILE) != 0) info |= 0x08;
        if (!forceGet && (q.Flags & V4L2_CTRL_FLAG_WRITE_ONLY) != 0) info &= ~0x01;
        return (byte)info;
    }

    protected static UvcException Unsupported() => new("Not supported by uvcvideo");

    public static readonly IReadOnlyDictionary<byte, V4L2Mapping> CameraTerminal = new Dictionary<byte, V4L2Mapping>
    {
        [0x02] = new AeModeMapping(),
        [0x03] = new FieldsMapping((0, 1, false, CID_EXPOSURE_AUTO_PRIORITY)),
        [0x04] = new FieldsMapping((0, 4, false, CID_EXPOSURE_ABSOLUTE)),
        [0x06] = new FieldsMapping((0, 2, false, CID_FOCUS_ABSOLUTE)),
        [0x07] = new RelativeMapping((0, 1, CID_FOCUS_RELATIVE)),
        [0x08] = new FieldsMapping((0, 1, false, CID_FOCUS_AUTO)),
        [0x09] = new FieldsMapping((0, 2, false, CID_IRIS_ABSOLUTE)),
        [0x0A] = new RelativeMapping((0, -1, CID_IRIS_RELATIVE)),
        [0x0B] = new FieldsMapping((0, 2, false, CID_ZOOM_ABSOLUTE)),
        [0x0C] = new RelativeMapping((0, 2, CID_ZOOM_CONTINUOUS)),
        [0x0D] = new FieldsMapping((0, 4, true, CID_PAN_ABSOLUTE), (4, 4, true, CID_TILT_ABSOLUTE)),
        [0x0E] = new RelativeMapping((0, 1, CID_PAN_SPEED), (2, 3, CID_TILT_SPEED)),
        [0x11] = new FieldsMapping((0, 1, false, CID_PRIVACY)),
    };

    public static readonly IReadOnlyDictionary<byte, V4L2Mapping> ProcessingUnit = new Dictionary<byte, V4L2Mapping>
    {
        [0x01] = new FieldsMapping((0, 2, false, CID_BACKLIGHT_COMPENSATION)),
        [0x02] = new FieldsMapping((0, 2, true, CID_BRIGHTNESS)),
        [0x03] = new FieldsMapping((0, 2, false, CID_CONTRAST)),
        [0x04] = new FieldsMapping((0, 2, false, CID_GAIN)),
        [0x05] = new FieldsMapping((0, 1, false, CID_POWER_LINE_FREQUENCY)),
        [0x06] = new FieldsMapping((0, 2, true, CID_HUE)),
        [0x07] = new FieldsMapping((0, 2, false, CID_SATURATION)),
        [0x08] = new FieldsMapping((0, 2, false, CID_SHARPNESS)),
        [0x09] = new FieldsMapping((0, 2, false, CID_GAMMA)),
        [0x0A] = new FieldsMapping((0, 2, false, CID_WHITE_BALANCE_TEMPERATURE)),
        [0x0B] = new FieldsMapping((0, 1, false, CID_AUTO_WHITE_BALANCE)),
        [0x0C] = new FieldsMapping((0, 2, false, CID_BLUE_BALANCE), (2, 2, false, CID_RED_BALANCE)),
        [0x10] = new FieldsMapping((0, 1, false, CID_HUE_AUTO)),
    };
}

/// <summary>Each UVC field is one V4L2 control holding the same value.</summary>
[SupportedOSPlatform("linux")]
internal sealed class FieldsMapping(params (int Offset, int Size, bool Signed, uint Cid)[] fields) : V4L2Mapping
{
    public override byte[] Get(LinuxUvcTransport t, UvcRequest request, int length)
    {
        if (request == UvcRequest.GetInfo) return [Info(t.QueryControl(fields[0].Cid))];
        if (request == UvcRequest.GetLen) throw Unsupported();

        var buffer = new byte[length];
        foreach (var (offset, size, _, cid) in fields)
        {
            var q = t.QueryControl(cid);
            var value = request switch
            {
                UvcRequest.GetCur => (q.Flags & V4L2_CTRL_FLAG_WRITE_ONLY) != 0 ? 0 : t.GetControl(cid),
                UvcRequest.GetMin => q.Minimum,
                UvcRequest.GetMax => q.Maximum,
                UvcRequest.GetRes => Math.Max(q.Step, 1),
                UvcRequest.GetDef => q.DefaultValue,
                _ => throw Unsupported(),
            };
            if (offset + size <= length) buffer.WriteInt(value, offset, size);
        }
        return buffer;
    }

    public override void Set(LinuxUvcTransport t, byte[] payload)
    {
        foreach (var (offset, size, signed, cid) in fields)
            t.SetControl(cid, (int)payload.ReadInt(offset, size, signed));
    }
}

/// <summary>CT_AE_MODE ↔ V4L2_CID_EXPOSURE_AUTO, whose menu indices map to UVC mode bits.</summary>
[SupportedOSPlatform("linux")]
internal sealed class AeModeMapping : V4L2Mapping
{
    // V4L2_EXPOSURE_AUTO, _MANUAL, _SHUTTER_PRIORITY, _APERTURE_PRIORITY
    private static readonly byte[] ModeBits = [0x02, 0x01, 0x04, 0x08];

    public override byte[] Get(LinuxUvcTransport t, UvcRequest request, int length)
    {
        var q = t.QueryControl(CID_EXPOSURE_AUTO);
        return request switch
        {
            UvcRequest.GetInfo => [Info(q)],
            UvcRequest.GetCur => [Bit(t.GetControl(CID_EXPOSURE_AUTO))],
            UvcRequest.GetDef => [Bit(q.DefaultValue)],
            UvcRequest.GetRes => [(byte)Enumerable.Range(0, ModeBits.Length)
                .Where(i => i >= q.Minimum && i <= q.Maximum && t.MenuItemExists(CID_EXPOSURE_AUTO, (uint)i))
                .Aggregate(0, (bits, i) => bits | ModeBits[i])],
            _ => throw Unsupported(),
        };
    }

    public override void Set(LinuxUvcTransport t, byte[] payload)
    {
        var index = Array.IndexOf(ModeBits, payload[0]);
        if (index < 0) throw new UvcException($"Unknown auto-exposure mode 0x{payload[0]:X2}");
        t.SetControl(CID_EXPOSURE_AUTO, index);
    }

    private static byte Bit(int index) => index >= 0 && index < ModeBits.Length ? ModeBits[index] : (byte)0;
}

/// <summary>
/// Relative (move/step) controls. uvcvideo exposes each axis as one signed V4L2 value whose sign is the
/// direction and whose magnitude is the speed. A speed offset of -1 means the axis has no speed byte.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class RelativeMapping(params (int DirectionOffset, int SpeedOffset, uint Cid)[] axes) : V4L2Mapping
{
    public override byte[] Get(LinuxUvcTransport t, UvcRequest request, int length)
    {
        var first = t.QueryControl(axes[0].Cid);
        if (request == UvcRequest.GetInfo) return [Info(first, forceGet: true)];

        var buffer = new byte[length];
        foreach (var (_, speedOffset, cid) in axes)
        {
            if (speedOffset < 0 || speedOffset >= length) continue;
            var q = t.QueryControl(cid);
            var maxSpeed = Math.Clamp(Math.Max(Math.Abs(q.Minimum), Math.Abs(q.Maximum)), 1, 255);
            buffer[speedOffset] = request switch
            {
                UvcRequest.GetMin => 1,
                UvcRequest.GetMax or UvcRequest.GetDef => (byte)maxSpeed,
                UvcRequest.GetRes => 1,
                _ => 0,
            };
        }
        return buffer;
    }

    public override void Set(LinuxUvcTransport t, byte[] payload)
    {
        foreach (var (directionOffset, speedOffset, cid) in axes)
        {
            var q = t.QueryControl(cid);
            var direction = (sbyte)payload[directionOffset];
            var speed = speedOffset >= 0 && speedOffset < payload.Length ? Math.Max((int)payload[speedOffset], 1) : 1;
            t.SetControl(cid, Math.Clamp(Math.Sign(direction) * speed, q.Minimum, q.Maximum));
        }
    }
}
