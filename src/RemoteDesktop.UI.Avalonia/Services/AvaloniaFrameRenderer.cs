using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteDesktop.Capture;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.Avalonia.Services;

/// <summary>
/// Draws decoded frames into a <see cref="WriteableBitmap"/>. Only the newest frame matters, so if the UI thread is
/// busy older frames are replaced instead of queued (a slow UI must not build up a backlog of stale pictures).
/// </summary>
public sealed class AvaloniaFrameRenderer : IViewerRenderer
{
    private readonly object _lock = new();
    private byte[] _latest = [];
    private int _width, _height;
    private int _flushScheduled;

    public WriteableBitmap? Bitmap { get; private set; }

    public (int Width, int Height) FrameSize { get { lock (_lock) return (_width, _height); } }

    public long FramesDrawn { get; private set; }

    /// <summary>Raised on the UI thread when the bitmap was (re)created, e.g. the remote resolution changed.</summary>
    public event Action<WriteableBitmap>? BitmapChanged;

    /// <summary>Raised on the UI thread after every frame was written, so the picture control can repaint.</summary>
    public event Action? FrameDrawn;

    public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default)
    {
        var length = frame.Width * frame.Height * 4;
        lock (_lock)
        {
            // The frame's memory is pooled and returns to the pool when this call ends: copy it.
            if (_latest.Length != length) _latest = new byte[length];
            frame.PixelData.Span[..length].CopyTo(_latest);
            _width = frame.Width;
            _height = frame.Height;
        }

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
        return Task.CompletedTask;
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        lock (_lock)
        {
            if (_width <= 0 || _height <= 0) return;
            var created = false;
            if (Bitmap is null || Bitmap.PixelSize.Width != _width || Bitmap.PixelSize.Height != _height)
            {
                // The capture is opaque BGRA; alpha is ignored.
                Bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                created = true;
            }
            using (var locked = Bitmap.Lock())
            {
                for (var y = 0; y < _height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(_latest, y * _width * 4, locked.Address + y * locked.RowBytes, _width * 4);
            }
            FramesDrawn++;
            if (created) BitmapChanged?.Invoke(Bitmap);
            FrameDrawn?.Invoke();
        }
    }
}
