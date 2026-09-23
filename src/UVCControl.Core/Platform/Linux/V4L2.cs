using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UVCControl.Core.Platform.Linux;

[SupportedOSPlatform("linux")]
internal static unsafe partial class LibC
{
    public const int O_RDWR = 0x2;
    public const int O_NONBLOCK = 0x800;

    public const int EINVAL = 22;
    public const int EACCES = 13;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, void* arg);

    [LibraryImport("libc", EntryPoint = "strerror")]
    private static partial IntPtr StrError(int errno);

    public static string ErrorString(int errno) => Marshal.PtrToStringUTF8(StrError(errno)) ?? $"errno {errno}";
}

/// <summary>V4L2 and uvcvideo ioctl definitions (linux/videodev2.h, linux/uvcvideo.h).</summary>
[SupportedOSPlatform("linux")]
internal static class V4L2
{
    // _IOC(dir, type, nr, size) = dir << 30 | size << 16 | type << 8 | nr
    public const nuint VIDIOC_QUERYCAP = 0x80685600;   // _IOR('V', 0, v4l2_capability)
    public const nuint VIDIOC_G_CTRL = 0xC008561B;     // _IOWR('V', 27, v4l2_control)
    public const nuint VIDIOC_S_CTRL = 0xC008561C;     // _IOWR('V', 28, v4l2_control)
    public const nuint VIDIOC_QUERYCTRL = 0xC0445624;  // _IOWR('V', 36, v4l2_queryctrl)
    public const nuint VIDIOC_QUERYMENU = 0xC02C5625;  // _IOWR('V', 37, v4l2_querymenu)
    public const nuint UVCIOC_CTRL_QUERY = 0xC0107521; // _IOWR('u', 0x21, uvc_xu_control_query)

    public const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    public const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;

    public const uint V4L2_CTRL_FLAG_DISABLED = 0x0001;
    public const uint V4L2_CTRL_FLAG_READ_ONLY = 0x0004;
    public const uint V4L2_CTRL_FLAG_INACTIVE = 0x0010;
    public const uint V4L2_CTRL_FLAG_WRITE_ONLY = 0x0040;
    public const uint V4L2_CTRL_FLAG_VOLATILE = 0x0080;

    // User class
    public const uint CID_BRIGHTNESS = 0x00980900;
    public const uint CID_CONTRAST = 0x00980901;
    public const uint CID_SATURATION = 0x00980902;
    public const uint CID_HUE = 0x00980903;
    public const uint CID_AUTO_WHITE_BALANCE = 0x0098090C;
    public const uint CID_RED_BALANCE = 0x0098090E;
    public const uint CID_BLUE_BALANCE = 0x0098090F;
    public const uint CID_GAMMA = 0x00980910;
    public const uint CID_GAIN = 0x00980913;
    public const uint CID_POWER_LINE_FREQUENCY = 0x00980918;
    public const uint CID_HUE_AUTO = 0x00980919;
    public const uint CID_WHITE_BALANCE_TEMPERATURE = 0x0098091A;
    public const uint CID_SHARPNESS = 0x0098091B;
    public const uint CID_BACKLIGHT_COMPENSATION = 0x0098091C;

    // Camera class
    public const uint CID_EXPOSURE_AUTO = 0x009A0901;
    public const uint CID_EXPOSURE_ABSOLUTE = 0x009A0902;
    public const uint CID_EXPOSURE_AUTO_PRIORITY = 0x009A0903;
    public const uint CID_PAN_ABSOLUTE = 0x009A0908;
    public const uint CID_TILT_ABSOLUTE = 0x009A0909;
    public const uint CID_FOCUS_ABSOLUTE = 0x009A090A;
    public const uint CID_FOCUS_RELATIVE = 0x009A090B;
    public const uint CID_FOCUS_AUTO = 0x009A090C;
    public const uint CID_ZOOM_ABSOLUTE = 0x009A090D;
    public const uint CID_ZOOM_CONTINUOUS = 0x009A090F;
    public const uint CID_PRIVACY = 0x009A0910;
    public const uint CID_IRIS_ABSOLUTE = 0x009A0911;
    public const uint CID_IRIS_RELATIVE = 0x009A0912;
    public const uint CID_PAN_SPEED = 0x009A0920;
    public const uint CID_TILT_SPEED = 0x009A0921;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct V4L2Capability
{
    public fixed byte Driver[16];
    public fixed byte Card[32];
    public fixed byte BusInfo[32];
    public uint Version;
    public uint Capabilities;
    public uint DeviceCaps;
    public fixed uint Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct V4L2QueryCtrl
{
    public uint Id;
    public uint Type;
    public fixed byte Name[32];
    public int Minimum;
    public int Maximum;
    public int Step;
    public int DefaultValue;
    public uint Flags;
    public fixed uint Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal struct V4L2Control
{
    public uint Id;
    public int Value;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct V4L2QueryMenu
{
    public uint Id;
    public uint Index;
    public fixed byte Name[32];
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UvcXuControlQuery
{
    public byte Unit;
    public byte Selector;
    public byte Query;
    public ushort Size;
    public IntPtr Data;
}
