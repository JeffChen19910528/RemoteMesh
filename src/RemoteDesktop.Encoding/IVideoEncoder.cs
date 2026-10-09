using RemoteDesktop.Capture;

namespace RemoteDesktop.Encoding;

public interface IVideoEncoder : IAsyncDisposable
{
    CodecType Codec { get; }
    VideoQuality Quality { get; set; }
    /// <summary>
    /// Encodes one frame. An empty result means "nothing changed, send nothing": the viewer already shows this
    /// picture, and the encoder has not used a sequence number for it.
    /// </summary>
    Task<ReadOnlyMemory<byte>> EncodeAsync(CaptureFrame frame, CancellationToken ct = default);

    /// <summary>
    /// Whether the frame returned by the last <see cref="EncodeAsync"/> can be decoded on its own.
    /// Intra-only codecs (the default) produce only such frames.
    /// </summary>
    bool LastFrameWasKeyframe => true;
}

/// <summary>Implemented by encoders whose frames depend on earlier frames: lets the receiver ask for a fresh start.</summary>
public interface IKeyframeControl
{
    /// <summary>The next encoded frame will be a self-contained keyframe.</summary>
    void ForceKeyframe();
}
