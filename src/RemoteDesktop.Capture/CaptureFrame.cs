namespace RemoteDesktop.Capture;

/// <summary>
/// One BGRA frame. When <see cref="Owner"/> is set the pixel memory is borrowed from a pool: it is valid only
/// until <see cref="Dispose"/>, so consumers that need the pixels longer must copy them.
/// </summary>
public sealed class CaptureFrame : IDisposable
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ReadOnlyMemory<byte> PixelData { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public long FrameNumber { get; init; }

    /// <summary>Releases the pixel buffer back to its pool (idempotent). Null for frames that own plain arrays.</summary>
    public IDisposable? Owner { get; init; }

    public void Dispose() => Owner?.Dispose();
}
