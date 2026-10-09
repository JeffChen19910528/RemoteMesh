using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Streaming;

/// <summary>Viewer side: Desktop channel → reassemble → decode → render. Bad frames are skipped, not fatal.</summary>
public sealed class DesktopStreamReceiver
{
    private readonly IChannel _channel;
    private readonly Dictionary<byte, IVideoDecoder> _decoders = new();
    private readonly IFrameRenderer _renderer;
    private readonly FrameReassembler _reassembler = new();

    public DesktopStreamReceiver(IChannel channel, IVideoDecoder decoder, IFrameRenderer renderer)
        : this(channel, [decoder], renderer) { }

    /// <summary>Accepts every codec in <paramref name="decoders"/>; each frame is decoded by the one it declares.</summary>
    public DesktopStreamReceiver(IChannel channel, IEnumerable<IVideoDecoder> decoders, IFrameRenderer renderer)
    {
        if (channel.Type != ChannelType.Desktop)
            throw new ArgumentException("Video must be received on the Desktop channel.", nameof(channel));
        foreach (var d in decoders) _decoders[(byte)d.Codec] = d;
        if (_decoders.Count == 0) throw new ArgumentException("At least one decoder is required.", nameof(decoders));
        _channel = channel;
        _renderer = renderer;
    }

    public long FramesRendered { get; private set; }

    /// <summary>Encoded video bytes received so far (what the connection actually carried).</summary>
    public long BytesReceived { get; private set; }

    /// <summary>The codec of the most recent frame, once one has arrived.</summary>
    public CodecType? LastCodec { get; private set; }
    public long FramesCorrupt { get; private set; }

    /// <summary>Delta frames that could not be applied because the reference frame was missing or a frame was lost.</summary>
    public long FramesAwaitingKeyframe { get; private set; }

    /// <summary>
    /// Raised when the decoder needs a keyframe. The session layer should forward it to the sender
    /// (<see cref="DesktopStreamSender.RequestKeyframe"/>) over the control channel, rate limited.
    /// </summary>
    public event Action? KeyframeNeeded;
    public long FramesDropped => _reassembler.DroppedFrames;

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var raw in _channel.ReceiveAsync(ct))
            {
                var frame = _reassembler.Add(raw);
                if (frame is null || !_decoders.TryGetValue(frame.Codec, out var decoder)) continue; // a codec we did not offer: ignore
                BytesReceived += frame.Data.Length;
                LastCodec = decoder.Codec;
                try
                {
                    // Decoded pixels are pooled: they are valid for the duration of RenderAsync only.
                    using var decoded = await decoder.DecodeAsync(frame.Data, ct);
                    await _renderer.RenderAsync(decoded, ct);
                    FramesRendered++;
                }
                catch (KeyframeRequiredException)
                {
                    FramesAwaitingKeyframe++;
                    KeyframeNeeded?.Invoke();
                }
                catch (InvalidDataException) { FramesCorrupt++; }
            }
        }
        catch (OperationCanceledException) { }
    }
}
