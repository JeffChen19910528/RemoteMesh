using System.Buffers.Binary;
using RemoteDesktop.Capture;
using RemoteDesktop.Encoding;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Streaming;

/// <summary>Host side: capture → encode → packetize → Desktop channel. Frame content is never logged.</summary>
public sealed class DesktopStreamSender
{
    private readonly IScreenCapture _capture;
    private readonly IVideoEncoder _encoder;
    private readonly IChannel _channel;
    private long _frameNumber;

    public DesktopStreamSender(IScreenCapture capture, IVideoEncoder encoder, IChannel channel)
    {
        if (channel.Type != ChannelType.Desktop)
            throw new ArgumentException("Video must be sent on the Desktop channel.", nameof(channel));
        _capture = capture;
        _encoder = encoder;
        _channel = channel;
    }

    public long FramesSent => _frameNumber;
    public long BytesSent { get; private set; }

    /// <summary>Called by the adaptive controller; takes effect on the next frame.</summary>
    public void ApplyQuality(VideoQuality quality) => _encoder.Quality = quality;

    /// <summary>Asks a delta-based encoder to start over with a self-contained frame (viewer lost sync).</summary>
    public void RequestKeyframe()
    {
        if (_encoder is IKeyframeControl k) k.ForceKeyframe();
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _capture.StartAsync(ct);
        try
        {
            await foreach (var frame in _capture.GetFramesAsync(ct))
            {
                var encoded = await _encoder.EncodeAsync(frame, ct);
                var w = BinaryPrimitives.ReadUInt16BigEndian(encoded.Span);
                var h = BinaryPrimitives.ReadUInt16BigEndian(encoded.Span[2..]);
                foreach (var packet in FramePacketizer.Split(_frameNumber, (byte)_encoder.Codec,
                             isKeyframe: _encoder.LastFrameWasKeyframe, w, h, encoded))
                {
                    await _channel.SendAsync(packet, ct);
                    BytesSent += packet.Length;
                }
                _frameNumber++;
            }
        }
        catch (OperationCanceledException) { }
        finally { await _capture.StopAsync(CancellationToken.None); }
    }
}
