using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RemoteDesktop.Capture;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Captures the main display with CoreGraphics. macOS asks the user once for "Screen Recording" permission
/// (System Settings → Privacy &amp; Security); without it the picture contains only the wallpaper, so
/// <see cref="MacPermissions"/> lets the application tell the user what is missing. A Retina display is captured at
/// its full pixel size and reduced by a whole factor so frames stay a size the pipeline can encode in real time.
/// Not captured: the mouse cursor (the viewer draws its own).
/// </summary>
public sealed class CoreGraphicsScreenCapture : IScreenCapture
{
    /// <summary>Frames wider than this are reduced (box filter) by the smallest whole factor that fits.</summary>
    public const int DefaultMaxWidth = 2560;

    private readonly int _fps;
    private readonly int _maxWidth;

    public CoreGraphicsScreenCapture(int fps = 20, int maxWidth = DefaultMaxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fps, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 60);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 320);
        _fps = fps;
        _maxWidth = maxWidth;
    }

    public bool IsCapturing { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CaptureFrame> GetFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The CoreGraphics backend only runs on macOS.");

        var interval = TimeSpan.FromSeconds(1.0 / _fps);
        var clock = Stopwatch.StartNew();
        long n = 0;
        while (IsCapturing && !ct.IsCancellationRequested)
        {
            var frame = Grab(n++, _maxWidth);
            if (frame is not null) yield return frame;

            var next = TimeSpan.FromTicks(interval.Ticks * n);
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        IsCapturing = false;
        return ValueTask.CompletedTask;
    }

    private static CaptureFrame? Grab(long number, int maxWidth)
    {
        var image = MacNative.CGDisplayCreateImage(MacNative.CGMainDisplayID());
        if (image == IntPtr.Zero) return null;
        IntPtr data = IntPtr.Zero;
        try
        {
            var width = (int)MacNative.CGImageGetWidth(image);
            var height = (int)MacNative.CGImageGetHeight(image);
            var rowBytes = (int)MacNative.CGImageGetBytesPerRow(image);
            // Only the usual layout is handled: 32 bits per pixel, little-endian, alpha first = BGRA in memory.
            if (width <= 0 || height <= 0 || MacNative.CGImageGetBitsPerPixel(image) != 32 || rowBytes < width * 4) return null;
            if ((MacNative.CGImageGetBitmapInfo(image) & 0x7000) != 0x2000) return null; // kCGBitmapByteOrder32Little

            data = MacNative.CGDataProviderCopyData(MacNative.CGImageGetDataProvider(image));
            if (data == IntPtr.Zero) return null;
            var length = (long)MacNative.CFDataGetLength(data);
            if (length < (long)(height - 1) * rowBytes + width * 4L) return null;

            var factor = ScaleFactor(width, maxWidth);
            var outW = width / factor;
            var outH = height / factor;
            if (outW <= 0 || outH <= 0) return null;

            var owner = new PooledPixels(outW * outH * 4);
            unsafe
            {
                var source = new ReadOnlySpan<byte>((void*)MacNative.CFDataGetBytePtr(data), (int)length);
                Downscale(source, width, height, rowBytes, factor, owner.Array.AsSpan(0, outW * outH * 4));
            }
            return new CaptureFrame
            {
                Width = outW,
                Height = outH,
                PixelData = owner.Array.AsMemory(0, outW * outH * 4),
                FrameNumber = number,
                Owner = owner
            };
        }
        finally
        {
            if (data != IntPtr.Zero) MacNative.CFRelease(data);
            MacNative.CGImageRelease(image);
        }
    }

    /// <summary>The smallest whole factor that brings <paramref name="width"/> to at most <paramref name="maxWidth"/>.</summary>
    public static int ScaleFactor(int width, int maxWidth) => width <= maxWidth ? 1 : (width + maxWidth - 1) / maxWidth;

    /// <summary>
    /// Reduces BGRA pixels by a whole <paramref name="factor"/> with a box filter (each output pixel is the average of
    /// a factor×factor block) and forces the result opaque. Pure and allocation-free, so it is tested on every system.
    /// </summary>
    public static void Downscale(ReadOnlySpan<byte> source, int width, int height, int rowBytes, int factor, Span<byte> destination)
    {
        var outW = width / factor;
        var outH = height / factor;
        if (destination.Length < outW * outH * 4) throw new ArgumentException("The destination is too small.", nameof(destination));
        var samples = factor * factor;

        for (var y = 0; y < outH; y++)
            for (var x = 0; x < outW; x++)
            {
                int b = 0, g = 0, r = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = (y * factor + dy) * rowBytes + x * factor * 4;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var i = row + dx * 4;
                        b += source[i]; g += source[i + 1]; r += source[i + 2];
                    }
                }
                var o = (y * outW + x) * 4;
                destination[o] = (byte)((b + samples / 2) / samples);
                destination[o + 1] = (byte)((g + samples / 2) / samples);
                destination[o + 2] = (byte)((r + samples / 2) / samples);
                destination[o + 3] = 255;
            }
    }

    private sealed class PooledPixels(int length) : IDisposable
    {
        private byte[]? _array = ArrayPool<byte>.Shared.Rent(length);
        public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledPixels));

        public void Dispose()
        {
            var a = Interlocked.Exchange(ref _array, null);
            if (a is not null) ArrayPool<byte>.Shared.Return(a);
        }
    }
}
