using RemoteDesktop.Capture;

namespace RemoteDesktop.Streaming;

public interface IFrameRenderer
{
    /// <summary>
    /// Draws the frame. The frame's pixel memory is pooled and is released as soon as this returns, so a
    /// renderer that keeps pixels (for example to draw later) must copy them first.
    /// </summary>
    Task RenderAsync(CaptureFrame frame, CancellationToken ct = default);
}
