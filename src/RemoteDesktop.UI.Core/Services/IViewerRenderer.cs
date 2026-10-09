using RemoteDesktop.Streaming;

namespace RemoteDesktop.UI.Services;

/// <summary>A picture sink the viewer window draws from; each UI toolkit has its own implementation.</summary>
public interface IViewerRenderer : IFrameRenderer
{
    /// <summary>Size of the last frame (for mapping mouse positions back to the remote screen).</summary>
    (int Width, int Height) FrameSize { get; }
}
