using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Capture;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// Captures the primary monitor with GDI BitBlt. It works on every Windows version with no extra dependencies,
/// which makes it the right first backend; Desktop Duplication / Windows Graphics Capture can replace it later
/// behind <see cref="IScreenCapture"/> for higher frame rates and lower CPU.
/// Not captured: the mouse cursor (the viewer draws its own) and secure desktops (UAC prompts, lock screen).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapture : IScreenCapture
{
    private readonly int _fps;

    public GdiScreenCapture(int fps = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fps, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 60);
        _fps = fps;
        DpiAwareness.Ensure();
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
        using var grabber = new Grabber();
        var interval = TimeSpan.FromSeconds(1.0 / _fps);
        var clock = Stopwatch.StartNew();
        long n = 0;

        while (IsCapturing && !ct.IsCancellationRequested)
        {
            var frame = grabber.Grab(n++);
            if (frame is not null) yield return frame;

            // Fixed cadence: sleep only for what is left of this frame's slot.
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

    /// <summary>
    /// Owns the memory DC and bitmap, recreated when the screen size changes. The screen DC is NOT kept: it is
    /// thread-affine, and an async iterator can resume on a different thread after each delay, so it is acquired
    /// and released within a single synchronous <see cref="Grab"/> call.
    /// </summary>
    private sealed class Grabber : IDisposable
    {
        private IntPtr _memDc, _bitmap, _oldBitmap, _bits;
        private int _width, _height;

        public int LastError { get; private set; }

        public CaptureFrame? Grab(long number)
        {
            var w = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            var h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            if (w <= 0 || h <= 0) return null;

            var screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            try
            {
                if (w != _width || h != _height || _memDc == IntPtr.Zero) Recreate(screenDc, w, h);

                if (!Native.BitBlt(_memDc, 0, 0, w, h, screenDc, 0, 0, Native.SRCCOPY | Native.CAPTUREBLT))
                {
                    // e.g. a secure desktop is showing: skip this frame instead of ending the stream, and rebuild
                    // the memory DC next time so a stale one cannot keep failing.
                    LastError = Marshal.GetLastWin32Error();
                    Release();
                    return null;
                }
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }

            var length = w * h * 4;
            var owner = new PooledPixels(length);
            Marshal.Copy(_bits, owner.Array, 0, length);

            // GDI leaves alpha at 0; the pipeline treats frames as opaque BGRA.
            var pixels = MemoryMarshal.Cast<byte, uint>(owner.Array.AsSpan(0, length));
            for (var i = 0; i < pixels.Length; i++) pixels[i] |= 0xFF000000u;

            return new CaptureFrame
            {
                Width = w,
                Height = h,
                PixelData = owner.Array.AsMemory(0, length),
                FrameNumber = number,
                Owner = owner
            };
        }

        private void Recreate(IntPtr screenDc, int w, int h)
        {
            Release();
            _memDc = Native.CreateCompatibleDC(screenDc);
            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down rows, like every other frame in the pipeline
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };
            _bitmap = Native.CreateDIBSection(screenDc, ref info, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not create the capture bitmap.");
            _oldBitmap = Native.SelectObject(_memDc, _bitmap);
            _width = w;
            _height = h;
        }

        private void Release()
        {
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) Native.SelectObject(_memDc, _oldBitmap);
                Native.DeleteDC(_memDc);
            }
            if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
            _memDc = _bitmap = _oldBitmap = _bits = IntPtr.Zero;
            _width = _height = 0;
        }

        public void Dispose() => Release();
    }

    /// <summary>Pixel memory rented from the shared pool; the consumer returns it by disposing the frame.</summary>
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

/// <summary>Without DPI awareness Windows reports scaled (virtualized) metrics and the capture would be blurry or cropped.</summary>
internal static class DpiAwareness
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0) Native.SetProcessDPIAware();
    }
}
