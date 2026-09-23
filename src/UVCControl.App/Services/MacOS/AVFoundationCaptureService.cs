using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using UVCControl.Core.Transport;
using static UVCControl.App.Services.MacOS.MediaNative;

namespace UVCControl.App.Services.MacOS;

/// <summary>Video capture straight through AVFoundation, mirroring the Swift app's capture model.</summary>
/// <remarks>
/// Lists the device's native formats (420v, yuvs, MJPEG, …) and always asks AVFoundation for BGRA output,
/// so every format is converted by the system. The format is applied after the session starts, because
/// starting a session resets the device to the session preset.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class AVFoundationCaptureService(ILogger<AVFoundationCaptureService> logger) : ICameraCaptureService
{
    internal static readonly IntPtr AVFoundationLib =
        NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");

    public Task<ICameraSource?> FindAsync(UvcDeviceInfo device, CancellationToken ct = default) => Task.Run(() =>
    {
        using var pool = ObjC.Pool();
        var mediaVideo = ObjC.StringConstant(AVFoundationLib, "AVMediaTypeVideo");
        var captureDevice = ObjC.GetClass("AVCaptureDevice");

        // AVAuthorizationStatusRestricted = 1, Denied = 2. NotDetermined prompts when the input is created.
        if (ObjC.SendNInt(captureDevice, "authorizationStatusForMediaType:", mediaVideo) is 1 or 2)
            throw new UnauthorizedAccessException("Camera access denied — allow it in System Settings › Privacy & Security › Camera.");

        // UVC devices report a modelID like "UVC Camera VendorID_11427 ProductID_35".
        var pattern = $"VendorID_{device.VendorId} ProductID_{device.ProductId}";
        foreach (var candidate in ObjC.Items(ExternalDevices(mediaVideo)))
        {
            var modelId = ObjC.ToManagedString(ObjC.Send(candidate, "modelID")) ?? "";
            if (!modelId.Contains(pattern, StringComparison.Ordinal)) continue;
            logger.LogDebug("Capture device for {Device}: {Model}", device.Name, modelId);
            return (ICameraSource?)new AVFoundationSource(candidate);
        }
        return null;
    }, ct);

    private static IntPtr ExternalDevices(IntPtr mediaVideo)
    {
        // AVCaptureDeviceTypeExternal is macOS 14+; older systems use the now-deprecated ExternalUnknown.
        var type = ObjC.StringConstant(AVFoundationLib, "AVCaptureDeviceTypeExternal");
        if (type == IntPtr.Zero) type = ObjC.StringConstant(AVFoundationLib, "AVCaptureDeviceTypeExternalUnknown");
        var types = ObjC.Send(ObjC.GetClass("NSArray"), "arrayWithObject:", type);
        var discovery = ObjC.Send(ObjC.GetClass("AVCaptureDeviceDiscoverySession"),
            "discoverySessionWithDeviceTypes:mediaType:position:", types, mediaVideo, 0);
        return ObjC.Send(discovery, "devices");
    }
}

/// <summary>An AVCaptureDevice and its native formats.</summary>
[SupportedOSPlatform("macos")]
internal sealed class AVFoundationSource : ICameraSource
{
    private readonly IntPtr _device;  // retained
    private readonly IntPtr _formats; // retained NSArray, keeps the format objects alive
    private bool _disposed;

    /// <remarks>Call inside an autorelease pool.</remarks>
    public AVFoundationSource(IntPtr device)
    {
        _device = ObjC.Retain(device);
        _formats = ObjC.Retain(ObjC.Send(device, "formats"));
        Name = ObjC.ToManagedString(ObjC.Send(device, "localizedName")) ?? "Camera";

        Formats = ObjC.Items(_formats).Select((format, index) =>
        {
            var description = ObjC.Send(format, "formatDescription");
            var dims = CMVideoFormatDescriptionGetDimensions(description);
            var rates = ObjC.Items(ObjC.Send(format, "videoSupportedFrameRateRanges"))
                .Select(range => Math.Round(ObjC.SendDouble(range, "maxFrameRate"), 2))
                .Distinct()
                .OrderDescending()
                .ToList();
            return new CameraFormat(index, dims.Width, dims.Height, FourCC(CMFormatDescriptionGetMediaSubType(description)), rates);
        }).ToList();
    }

    public string Name { get; }

    public IReadOnlyList<CameraFormat> Formats { get; }

    public Task<IAsyncDisposable> StartAsync(CameraFormat format, double frameRate, IFrameSink sink, CancellationToken ct = default) =>
        Task.Run<IAsyncDisposable>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var pool = ObjC.Pool();
            var nativeFormat = ObjC.SendIndex(_formats, "objectAtIndex:", (nuint)format.Id);
            return AVFoundationSession.Start(_device, nativeFormat, frameRate, sink);
        }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ObjC.Release(_formats);
        ObjC.Release(_device);
    }

    private static string FourCC(uint code)
    {
        Span<char> chars = [(char)(code >> 24 & 0xFF), (char)(code >> 16 & 0xFF), (char)(code >> 8 & 0xFF), (char)(code & 0xFF)];
        foreach (var c in chars)
            if (c is < ' ' or > '~') return $"0x{code:X8}";
        return new string(chars);
    }
}

/// <summary>A running AVCaptureSession delivering BGRA frames to an <see cref="IFrameSink"/>.</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class AVFoundationSession : IAsyncDisposable
{
    // Callbacks find their session through the output that produced the frame.
    private static readonly ConcurrentDictionary<IntPtr, AVFoundationSession> Active = new();
    private static readonly Lazy<IntPtr> Delegate = new(CreateDelegate);

    private readonly IFrameSink _sink;
    private IntPtr _session; // owned
    private IntPtr _output;  // owned
    private IntPtr _queue;   // owned
    private volatile bool _stopped;

    private AVFoundationSession(IFrameSink sink) => _sink = sink;

    /// <remarks>Call inside an autorelease pool.</remarks>
    public static AVFoundationSession Start(IntPtr device, IntPtr format, double frameRate, IFrameSink sink)
    {
        var s = new AVFoundationSession(sink);
        try
        {
            s._session = ObjC.Send(ObjC.Send(ObjC.GetClass("AVCaptureSession"), "alloc"), "init");

            IntPtr error = 0;
            var input = ObjC.Send(ObjC.GetClass("AVCaptureDeviceInput"), "deviceInputWithDevice:error:", device, (IntPtr)(&error));
            if (input == IntPtr.Zero)
                throw new InvalidOperationException(ObjC.ToManagedString(ObjC.Send(error, "localizedDescription")) ?? "Could not open the camera.");
            if (!ObjC.SendBool(s._session, "canAddInput:", input)) throw new InvalidOperationException("Could not add the camera input.");
            ObjC.Send(s._session, "addInput:", input);

            s._output = ObjC.Send(ObjC.Send(ObjC.GetClass("AVCaptureVideoDataOutput"), "alloc"), "init");
            var key = ObjC.StringConstant(NativeLibrary.Load("/System/Library/Frameworks/CoreVideo.framework/CoreVideo"),
                "kCVPixelBufferPixelFormatTypeKey");
            var bgra = ObjC.Send(ObjC.GetClass("NSNumber"), "numberWithUnsignedInt:", (IntPtr)PixelFormat32BGRA);
            ObjC.Send(s._output, "setVideoSettings:", ObjC.Send(ObjC.GetClass("NSDictionary"), "dictionaryWithObject:forKey:", bgra, key));
            ObjC.SendVoid(s._output, "setAlwaysDiscardsLateVideoFrames:", true);

            s._queue = DispatchQueueCreate("UVCControl.preview", IntPtr.Zero);
            Active[s._output] = s;
            ObjC.Send(s._output, "setSampleBufferDelegate:queue:", Delegate.Value, s._queue);
            if (!ObjC.SendBool(s._session, "canAddOutput:", s._output)) throw new InvalidOperationException("Could not add the video output.");
            ObjC.Send(s._session, "addOutput:", s._output);

            ObjC.Send(s._session, "startRunning");
            // Apply after starting, since starting resets the active format to the session preset.
            ApplyFormat(device, format, frameRate);
            return s;
        }
        catch
        {
            s.Stop();
            throw;
        }
    }

    private static void ApplyFormat(IntPtr device, IntPtr format, double frameRate)
    {
        // Only use a frame duration the format advertises; anything else raises an Objective-C exception.
        CMTime? duration = null;
        foreach (var range in ObjC.Items(ObjC.Send(format, "videoSupportedFrameRateRanges")))
        {
            if (Math.Abs(ObjC.SendDouble(range, "maxFrameRate") - frameRate) < 0.01)
                duration = ObjC.SendTime(range, "minFrameDuration");
        }

        if (!ObjC.SendBool(device, "lockForConfiguration:", IntPtr.Zero))
            throw new InvalidOperationException("Could not lock the camera for configuration.");
        try
        {
            ObjC.Send(device, "setActiveFormat:", format);
            if (duration is { } d)
            {
                ObjC.SendVoid(device, "setActiveVideoMinFrameDuration:", d);
                ObjC.SendVoid(device, "setActiveVideoMaxFrameDuration:", d);
            }
        }
        finally
        {
            ObjC.Send(device, "unlockForConfiguration");
        }
    }

    public ValueTask DisposeAsync() => new(Task.Run(Stop));

    private void Stop()
    {
        _stopped = true;
        using var pool = ObjC.Pool();
        if (_session != IntPtr.Zero)
        {
            if (ObjC.SendBool(_session, "isRunning")) ObjC.Send(_session, "stopRunning");
            ObjC.Release(_session);
            _session = IntPtr.Zero;
        }
        if (_output != IntPtr.Zero)
        {
            ObjC.Send(_output, "setSampleBufferDelegate:queue:", IntPtr.Zero, IntPtr.Zero);
            Active.TryRemove(_output, out _);
            ObjC.Release(_output);
            _output = IntPtr.Zero;
        }
        if (_queue != IntPtr.Zero)
        {
            DispatchRelease(_queue);
            _queue = IntPtr.Zero;
        }
    }

    /// <summary>Registers a sample-buffer delegate class once; the single instance lives for the whole process.</summary>
    private static IntPtr CreateDelegate()
    {
        const string name = "UVCControlSampleBufferDelegate";
        var cls = ObjC.GetClass(name);
        if (cls == IntPtr.Zero)
        {
            cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), name, 0);
            ObjC.AddMethod(cls, ObjC.Selector("captureOutput:didOutputSampleBuffer:fromConnection:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidOutputSampleBuffer, "v@:@@@");
            var protocol = ObjC.GetProtocol("AVCaptureVideoDataOutputSampleBufferDelegate");
            if (protocol != IntPtr.Zero) ObjC.AddProtocol(cls, protocol);
            ObjC.RegisterClassPair(cls);
        }
        using var pool = ObjC.Pool();
        return ObjC.Send(ObjC.Send(cls, "alloc"), "init");
    }

    /// <summary>Runs on the capture dispatch queue. Must never throw.</summary>
    [UnmanagedCallersOnly]
    private static void DidOutputSampleBuffer(IntPtr self, IntPtr selector, IntPtr output, IntPtr sampleBuffer, IntPtr connection)
    {
        try
        {
            if (!Active.TryGetValue(output, out var session) || session._stopped) return;
            var pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer);
            if (pixelBuffer == IntPtr.Zero || CVPixelBufferGetPixelFormatType(pixelBuffer) != PixelFormat32BGRA) return;

            if (CVPixelBufferLockBaseAddress(pixelBuffer, LockReadOnly) != 0) return;
            try
            {
                var width = (int)CVPixelBufferGetWidth(pixelBuffer);
                var height = (int)CVPixelBufferGetHeight(pixelBuffer);
                var stride = (int)CVPixelBufferGetBytesPerRow(pixelBuffer);
                var pixels = new ReadOnlySpan<byte>((void*)CVPixelBufferGetBaseAddress(pixelBuffer), stride * height);
                session._sink.WriteBgra(pixels, width, height, stride);
            }
            finally
            {
                CVPixelBufferUnlockBaseAddress(pixelBuffer, LockReadOnly);
            }
        }
        catch
        {
            // An exception escaping into the dispatch queue would terminate the process.
        }
    }
}
