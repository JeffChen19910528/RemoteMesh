namespace RemoteDesktop.Capture;

public interface IScreenCapture : IAsyncDisposable
{
    bool IsCapturing { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    IAsyncEnumerable<CaptureFrame> GetFramesAsync(CancellationToken ct = default);
}
