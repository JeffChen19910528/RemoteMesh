using System.Runtime.CompilerServices;

namespace RemoteDesktop.Capture;

/// <summary>
/// Deterministic BGRA test-pattern source. Stands in for the OS capture backends
/// (Desktop Duplication / WGC / PipeWire), which plug in behind <see cref="IScreenCapture"/>.
/// </summary>
public sealed class SyntheticScreenCapture : IScreenCapture
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int? _maxFrames;

    public SyntheticScreenCapture(int width, int height, int fps = 30, int? maxFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fps, 0);
        _width = width;
        _height = height;
        _fps = fps;
        _maxFrames = maxFrames;
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
        var interval = TimeSpan.FromSeconds(1.0 / _fps);
        for (long n = 0; IsCapturing && (_maxFrames is null || n < _maxFrames) && !ct.IsCancellationRequested; n++)
        {
            yield return new CaptureFrame
            {
                Width = _width,
                Height = _height,
                PixelData = Render(n),
                FrameNumber = n
            };
            await Task.Delay(interval, ct);
        }
    }

    private byte[] Render(long frame)
    {
        var data = new byte[_width * _height * 4];
        for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                var i = (y * _width + x) * 4;
                data[i] = (byte)(x * 255 / _width);
                data[i + 1] = (byte)(y * 255 / _height);
                data[i + 2] = (byte)(frame * 7);
                data[i + 3] = 255;
            }
        return data;
    }

    public ValueTask DisposeAsync()
    {
        IsCapturing = false;
        return ValueTask.CompletedTask;
    }
}
