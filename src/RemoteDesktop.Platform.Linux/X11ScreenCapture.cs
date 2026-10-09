using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RemoteDesktop.Capture;

namespace RemoteDesktop.Platform.Linux;

/// <summary>
/// Captures the whole X screen with XGetImage. Works on any X server (and on Wayland desktops through XWayland, where
/// it sees the X11 applications but not native Wayland windows); no extra packages beyond libX11.
/// Not captured: the mouse cursor (the viewer draws its own).
/// </summary>
public sealed class X11ScreenCapture : IScreenCapture
{
    private readonly int _fps;

    public X11ScreenCapture(int fps = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fps, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 60);
        _fps = fps;
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
        var display = X11Session.Open();
        try
        {
            var interval = TimeSpan.FromSeconds(1.0 / _fps);
            var clock = Stopwatch.StartNew();
            long n = 0;

            while (IsCapturing && !ct.IsCancellationRequested)
            {
                var frame = Grab(display, n++);
                if (frame is not null) yield return frame;

                var next = TimeSpan.FromTicks(interval.Ticks * n);
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
        finally { X11Native.XCloseDisplay(display); }
    }

    public ValueTask DisposeAsync()
    {
        IsCapturing = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>One screenshot as opaque BGRA, or null if the server refused (the next try may work).</summary>
    private static unsafe CaptureFrame? Grab(IntPtr display, long number)
    {
        var screen = X11Native.XDefaultScreen(display);
        var root = X11Native.XDefaultRootWindow(display);
        var w = X11Native.XDisplayWidth(display, screen);
        var h = X11Native.XDisplayHeight(display, screen);
        if (w <= 0 || h <= 0) return null;

        var imagePtr = X11Native.XGetImage(display, root, 0, 0, (uint)w, (uint)h, UIntPtr.MaxValue, X11Native.ZPixmap);
        if (imagePtr == IntPtr.Zero) return null;
        try
        {
            var image = Marshal.PtrToStructure<X11Native.XImage>(imagePtr);
            if (image.Data == IntPtr.Zero || image.Width != w || image.Height != h) return null;
            // The common case, and the only one handled: 32 bits per pixel, blue in the lowest byte (BGRX in memory).
            if (image.BitsPerPixel != 32 || image.BytesPerLine < w * 4 || image.RedMask != (UIntPtr)0xFF0000u
                || image.GreenMask != (UIntPtr)0x00FF00u || image.BlueMask != (UIntPtr)0x0000FFu || image.ByteOrder != 0)
                return null;

            var length = w * h * 4;
            var owner = new PooledPixels(length);
            for (var y = 0; y < h; y++)
                Marshal.Copy(image.Data + y * image.BytesPerLine, owner.Array, y * w * 4, w * 4);

            // X leaves the fourth byte undefined; the pipeline treats frames as opaque BGRA.
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
        finally { X11Native.XDestroyImage(imagePtr); }
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
