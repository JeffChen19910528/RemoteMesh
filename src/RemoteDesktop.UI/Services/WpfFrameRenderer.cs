using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesktop.Capture;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.UI.Services;

/// <summary>
/// Draws decoded frames into a <see cref="WriteableBitmap"/>. Only the newest frame matters, so if the UI thread
/// is busy older frames are replaced instead of queued (a slow UI must not build up a backlog of stale pictures).
/// </summary>
public sealed class WpfFrameRenderer(Dispatcher dispatcher) : IViewerRenderer
{
    private readonly object _lock = new();
    private byte[] _latest = [];
    private int _width, _height;
    private int _flushScheduled;

    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Size of the last frame (for mapping mouse positions back to the remote screen).</summary>
    public (int Width, int Height) FrameSize { get { lock (_lock) return (_width, _height); } }

    public long FramesDrawn { get; private set; }

    /// <summary>Raised on the UI thread when the bitmap was (re)created, e.g. the remote resolution changed.</summary>
    public event Action<WriteableBitmap>? BitmapChanged;

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
            dispatcher.BeginInvoke(Flush, DispatcherPriority.Render);
        return Task.CompletedTask;
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        lock (_lock)
        {
            if (_width <= 0 || _height <= 0) return;
            var created = false;
            if (Bitmap is null || Bitmap.PixelWidth != _width || Bitmap.PixelHeight != _height)
            {
                // Bgr32: the capture is opaque BGRA and alpha is ignored.
                Bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgr32, null);
                created = true;
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _latest, _width * 4, 0);
            FramesDrawn++;
            if (created) BitmapChanged?.Invoke(Bitmap);
        }
    }
}
