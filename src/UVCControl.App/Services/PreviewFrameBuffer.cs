using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UVCControl.App.Services;

/// <summary>
/// Converts frames from the capture thread into bitmaps for the UI thread.
/// Uncompressed frames are copied into two alternating <see cref="WriteableBitmap"/>s, so the
/// image control sees a new instance each frame and never draws a half-written one.
/// </summary>
public sealed class PreviewFrameBuffer : IFrameSink, IDisposable
{
    private readonly Lock _lock = new();
    private byte[] _pixels = [];
    private int _width, _height;
    private Bitmap? _encoded;
    private bool _hasFrame;
    private readonly WriteableBitmap?[] _targets = new WriteableBitmap?[2];
    private int _next;

    /// <summary>Raised on the capture thread after a frame was stored.</summary>
    public event Action? FrameWritten;

    public void WriteBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < stride * (height - 1) + width * 4) return;
        lock (_lock)
        {
            var rowBytes = Resize(width, height);
            for (var y = 0; y < height; y++)
                pixels.Slice(y * stride, rowBytes).CopyTo(_pixels.AsSpan(y * rowBytes, rowBytes));
            Stored();
        }
        FrameWritten?.Invoke();
    }

    public void WriteEncoded(ReadOnlySpan<byte> image)
    {
        if (image.Length > 54 && image[0] == (byte)'B' && image[1] == (byte)'M')
        {
            if (WriteBmp(image)) FrameWritten?.Invoke();
            return;
        }

        Bitmap decoded;
        try
        {
            using var stream = new MemoryStream(image.ToArray());
            decoded = new Bitmap(stream);
        }
        catch (Exception)
        {
            return;
        }
        lock (_lock)
        {
            _encoded?.Dispose();
            _encoded = decoded;
            _hasFrame = true;
        }
        FrameWritten?.Invoke();
    }

    private bool WriteBmp(ReadOnlySpan<byte> bmp)
    {
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(bmp[10..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(bmp[18..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bmp[22..]);
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bmp[28..]);
        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0;
        var srcStride = (width * bitsPerPixel + 31) / 32 * 4;
        if (width <= 0 || height <= 0 || bitsPerPixel is not (24 or 32) || dataOffset + srcStride * height > bmp.Length)
            return false;

        lock (_lock)
        {
            var rowBytes = Resize(width, height);
            for (var y = 0; y < height; y++)
            {
                var src = bmp.Slice(dataOffset + (bottomUp ? height - 1 - y : y) * srcStride, srcStride);
                var dst = _pixels.AsSpan(y * rowBytes, rowBytes);
                if (bitsPerPixel == 32)
                {
                    src[..rowBytes].CopyTo(dst);
                }
                else
                {
                    for (int x = 0, s = 0, d = 0; x < width; x++, s += 3, d += 4)
                    {
                        dst[d] = src[s];
                        dst[d + 1] = src[s + 1];
                        dst[d + 2] = src[s + 2];
                        dst[d + 3] = 0xFF;
                    }
                }
            }
            Stored();
        }
        return true;
    }

    /// <summary>Sizes the pixel store for a frame and returns its row length in bytes. Caller holds the lock.</summary>
    private int Resize(int width, int height)
    {
        if (_pixels.Length != width * height * 4) _pixels = new byte[width * height * 4];
        _width = width;
        _height = height;
        return width * 4;
    }

    private void Stored()
    {
        _encoded?.Dispose();
        _encoded = null;
        _hasFrame = true;
    }

    /// <summary>Returns the newest frame as a bitmap, or null when nothing new arrived. UI thread only.</summary>
    public Bitmap? Present()
    {
        lock (_lock)
        {
            if (!_hasFrame) return null;
            _hasFrame = false;
            if (_encoded is { } encoded)
            {
                _encoded = null;
                return encoded;
            }

            var size = new PixelSize(_width, _height);
            var target = _targets[_next];
            if (target is null || target.PixelSize != size)
            {
                target?.Dispose();
                target = _targets[_next] = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            _next ^= 1;

            using var locked = target.Lock();
            var rowBytes = _width * 4;
            for (var y = 0; y < _height; y++)
                Marshal.Copy(_pixels, y * rowBytes, locked.Address + y * locked.RowBytes, rowBytes);
            return target;
        }
    }

    /// <summary>Whether <paramref name="bitmap"/> is owned (and disposed) by this buffer.</summary>
    public bool Owns(Bitmap? bitmap) => bitmap is not null && (ReferenceEquals(bitmap, _targets[0]) || ReferenceEquals(bitmap, _targets[1]));

    public void Dispose()
    {
        lock (_lock)
        {
            _encoded?.Dispose();
            foreach (var target in _targets) target?.Dispose();
        }
    }
}
