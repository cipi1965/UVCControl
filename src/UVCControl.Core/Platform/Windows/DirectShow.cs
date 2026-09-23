using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace UVCControl.Core.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class DirectShow
{
    public static readonly Guid CLSID_SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    public static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    public static readonly Guid IID_IBaseFilter = new("56A86895-0AD4-11CE-B03A-0020AF0BA770");

    public const int FlagsAuto = 0x1;
    public const int FlagsManual = 0x2;

    /// <summary>KSPROPERTY_VIDCAP_CAMERACONTROL ids, passed straight through by IAMCameraControl.</summary>
    public enum CameraControl
    {
        Pan = 0, Tilt = 1, Roll = 2, Zoom = 3, Exposure = 4, Iris = 5, Focus = 6,
        ScanMode = 7, Privacy = 8,
        PanRelative = 10, TiltRelative = 11, RollRelative = 12, ZoomRelative = 13,
        ExposureRelative = 14, IrisRelative = 15, FocusRelative = 16,
        AutoExposurePriority = 19,
    }

    /// <summary>KSPROPERTY_VIDCAP_VIDEOPROCAMP ids, passed straight through by IAMVideoProcAmp.</summary>
    public enum VideoProcAmp
    {
        Brightness = 0, Contrast = 1, Hue = 2, Saturation = 3, Sharpness = 4, Gamma = 5,
        WhiteBalance = 7, BacklightCompensation = 8, Gain = 9,
        DigitalMultiplier = 10, DigitalMultiplierLimit = 11, PowerLineFrequency = 13,
    }
}

[ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICreateDevEnum
{
    [PreserveSig]
    int CreateClassEnumerator([In] ref Guid clsidDeviceClass, out IEnumMoniker? enumMoniker, int flags);
}

[ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag
{
    [PreserveSig]
    int Read([MarshalAs(UnmanagedType.LPWStr)] string propName, [MarshalAs(UnmanagedType.Struct)] out object? value,
             IntPtr errorLog);

    [PreserveSig]
    int Write([MarshalAs(UnmanagedType.LPWStr)] string propName, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>Common shape of IAMCameraControl and IAMVideoProcAmp.</summary>
internal interface IDirectShowPropertySet
{
    int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);
    int Set(int property, int value, int flags);
    int Get(int property, out int value, out int flags);
}

[ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAMCameraControl
{
    [PreserveSig]
    int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);

    [PreserveSig]
    int Set(int property, int value, int flags);

    [PreserveSig]
    int Get(int property, out int value, out int flags);
}

[ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAMVideoProcAmp
{
    [PreserveSig]
    int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);

    [PreserveSig]
    int Set(int property, int value, int flags);

    [PreserveSig]
    int Get(int property, out int value, out int flags);
}

[SupportedOSPlatform("windows")]
internal sealed class CameraControlSet(IAMCameraControl inner) : IDirectShowPropertySet
{
    public int GetRange(int p, out int min, out int max, out int step, out int def, out int caps) =>
        inner.GetRange(p, out min, out max, out step, out def, out caps);
    public int Set(int p, int value, int flags) => inner.Set(p, value, flags);
    public int Get(int p, out int value, out int flags) => inner.Get(p, out value, out flags);
}

[SupportedOSPlatform("windows")]
internal sealed class VideoProcAmpSet(IAMVideoProcAmp inner) : IDirectShowPropertySet
{
    public int GetRange(int p, out int min, out int max, out int step, out int def, out int caps) =>
        inner.GetRange(p, out min, out max, out step, out def, out caps);
    public int Set(int p, int value, int flags) => inner.Set(p, value, flags);
    public int Get(int p, out int value, out int flags) => inner.Get(p, out value, out flags);
}
