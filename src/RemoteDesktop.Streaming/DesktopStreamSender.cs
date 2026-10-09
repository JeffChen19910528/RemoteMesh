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
    private readonly RateController? _rate;
    private readonly TimeProvider _clock;
    private long _frameNumber;

    /// <param name="rate">When given, quality, picture size and frame rate follow the bandwidth budget it enforces.</param>
    public DesktopStreamSender(IScreenCapture capture, IVideoEncoder encoder, IChannel channel,
        RateController? rate = null, TimeProvider? clock = null)
    {
        if (channel.Type != ChannelType.Desktop)
            throw new ArgumentException("Video must be sent on the Desktop channel.", nameof(channel));
        _capture = capture;
        _encoder = encoder;
        _channel = channel;
        _rate = rate;
        _clock = clock ?? TimeProvider.System;
    }

    public long FramesSent => _frameNumber;
    public long BytesSent { get; private set; }

    /// <summary>Frames not sent because nothing on the screen had changed.</summary>
    public long FramesUnchanged { get; private set; }

    /// <summary>Frames dropped to respect the current frame-rate limit.</summary>
    public long FramesSkipped { get; private set; }

    /// <summary>The rate controller's current rung, if one is in use.</summary>
    public RateStep? CurrentStep => _rate?.Current;

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
        long lastStarted = 0;
        var any = false;
        try
        {
            await foreach (var captured in _capture.GetFramesAsync(ct))
            {
                // Captured frames are borrowed from a pool: give each one back as soon as it is encoded or skipped.
                using var frame = captured;

                if (_rate is not null)
                {
                    var step = _rate.Current;
                    var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, step.Fps));
                    // Measured from when the previous frame STARTED: encoding and sending already used up part of the interval.
                    if (any && _clock.GetElapsedTime(lastStarted) < interval) { FramesSkipped++; continue; }
                    if (_encoder is IRateAdaptive adaptive) adaptive.Apply(step, frame.Width, frame.Height);
                }

                lastStarted = _clock.GetTimestamp();
                any = true;
                var encoded = await _encoder.EncodeAsync(frame, ct);
                if (encoded.IsEmpty) { FramesUnchanged++; continue; } // nothing changed: nothing to send
                var w = BinaryPrimitives.ReadUInt16BigEndian(encoded.Span);
                var h = BinaryPrimitives.ReadUInt16BigEndian(encoded.Span[2..]);

                var sendStart = _clock.GetTimestamp();
                foreach (var packet in FramePacketizer.Split(_frameNumber, (byte)_encoder.Codec,
                             isKeyframe: _encoder.LastFrameWasKeyframe, w, h, encoded))
                {
                    await _channel.SendAsync(packet, ct);
                    BytesSent += packet.Length;
                }
                var sendTime = _clock.GetElapsedTime(sendStart);

                _frameNumber++;
                _rate?.Report(encoded.Length, sendTime);
            }
        }
        catch (OperationCanceledException) { }
        finally { await _capture.StopAsync(CancellationToken.None); }
    }
}
