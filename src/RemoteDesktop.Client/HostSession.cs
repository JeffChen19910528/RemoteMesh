using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.Input;
using RemoteDesktop.Protocol;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client;

/// <summary>
/// The controlled machine's side of a session: it streams the screen and serves input, clipboard and file
/// requests, each limited to what the host user granted (and the host's own policies).
/// </summary>
public sealed class HostSession : SessionBase
{
    private readonly SessionInfo _session;
    private DesktopStreamSender? _sender;
    private readonly IJpegCodec? _jpeg;
    private readonly int _budgetKbps;

    /// <summary>The video codec in use, once the stream has started (lossy only when both sides can do it).</summary>
    public CodecType? VideoCodec { get; private set; }

    private readonly Func<bool> _inputSuspended;

    internal HostSession(SessionId id, SessionEndpoint endpoint, ClientOptions options, Permission granted,
        DeviceId? peer, string? peerShortId, Func<bool> inputSuspended, Func<SessionId, string, Task>? closeOnServer)
        : base(id, endpoint, options, closeOnServer)
    {
        Peer = peer;
        PeerShortId = peerShortId;
        _inputSuspended = inputSuspended;
        _jpeg = options.JpegCodec;
        _budgetKbps = options.VideoBudgetKbps;
        _session = new SessionInfo
        {
            SessionId = id,
            CreatedAt = DateTimeOffset.UtcNow,
            // The server owns the real expiry and tells us when the session ends; this is only a backstop.
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            State = SessionState.Connected,
            Permissions = new PermissionSet(granted)
        };
    }

    /// <summary>
    /// Who is connected, when known. It is known for sessions this device approved; a session opened with a
    /// one-time code or by a trusted device does not tell the host who is on the other end, so it is null.
    /// </summary>
    public DeviceId? Peer { get; }

    /// <summary>The number the connected person knows this device by (their own short ID), when known.</summary>
    public string? PeerShortId { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public Permission Granted => _session.Permissions.Granted;

    /// <summary>Takes rights back right now (the host user can always restrict a live session).</summary>
    public void Revoke(Permission permission) => _session.Permissions.Revoke(permission);

    public long FramesSent => _sender?.FramesSent ?? 0;
    public long BytesSent => _sender?.BytesSent ?? 0;

    // Frames sent during the outage never arrived, so the next one must be a whole picture.
    protected override void OnReattached() => _sender?.RequestKeyframe();

    internal void Start(HostServices services)
    {
        if (services.Input is not null)
        {
            // While somebody is waiting for the user to approve them, existing viewers cannot act: otherwise a
            // viewer that was granted the mouse could click "Allow" on the prompt for itself or for a friend.
            var gate = new InputGate(_session, new SuspendableInput(services.Input, _inputSuspended));
            var receiver = new InputReceiver(Endpoint[ChannelType.Input], gate);
            Run("input", ct => receiver.RunAsync(ct));
        }

        if (services.Clipboard is not null)
        {
            var host = new ClipboardHost(Endpoint[ChannelType.Clipboard], _session, services.Clipboard);
            Run("clipboard", ct => host.RunAsync(ct));
        }

        if (services.FileRoot is not null)
        {
            var host = new FileTransferHost(Endpoint[ChannelType.File], _session, services.FileRoot);
            Run("file transfer", ct => host.RunAsync(ct));
        }

        if (services.CaptureFactory is not null && _session.Permissions.Has(Permission.ViewScreen))
        {
            // The viewer says which codecs it can decode right after it connects; lossy is used only if both sides
            // can do it, otherwise the lossless codec (always understood) carries the picture.
            var viewerHello = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            KeepAlive.ControlMessage += raw =>
            {
                var span = raw.Span;
                if (span.Length == 1 && span[0] == ControlTags.KeyframeRequest) _sender?.RequestKeyframe();
                else if (span.Length >= 1 && span.Length <= 16 && span[0] == ControlTags.Hello)
                    viewerHello.TrySetResult(span[1..].Contains((byte)CodecType.TileDeltaJpeg));
            };

            var createCapture = services.CaptureFactory;
            Run("screen", async ct =>
            {
                var lossy = false;
                if (_jpeg is not null)
                {
                    try { lossy = await viewerHello.Task.WaitAsync(TimeSpan.FromSeconds(3), ct); }
                    catch (TimeoutException) { /* an older viewer that never says hello: stay lossless */ }
                }

                IVideoEncoder encoder = lossy ? new TileDeltaJpegEncoder(_jpeg!) : new TileDeltaVideoEncoder();
                VideoCodec = encoder.Codec;
                // Only the lossy codec can trade picture quality for bandwidth, so only it gets a budget.
                var rate = lossy ? new RateController(_budgetKbps) : null;
                if (rate is not null) WatchLatency(rate);
                var sender = new DesktopStreamSender(createCapture(), encoder, Endpoint[ChannelType.Desktop], rate);
                _sender = sender;
                await sender.RunAsync(ct);
            });
        }

        StartKeepAliveAndWatchdog();
    }

    /// <summary>
    /// Lowers the lossy video's bandwidth budget ahead of the rate controller's own send-timing signal when round
    /// trips get slow, and restores it once they are good again for a while. Round-trip time is the one network
    /// quality signal available end to end over this transport (packet loss and raw bandwidth are not observable at
    /// this layer), so that is the only axis of <see cref="NetworkStats"/> this feeds with a real measurement; the
    /// budget ceiling itself stands in for "bandwidth" so a low configured budget still picks a matching preset.
    /// </summary>
    private void WatchLatency(RateController rate)
    {
        var quality = new AdaptiveQualityController();
        KeepAlive.RttMeasured += rtt =>
        {
            if (quality.Update(new NetworkStats(rtt.TotalMilliseconds, LossPercent: 0, BandwidthKbps: _budgetKbps)))
                rate.AdjustBudgetKbps(Math.Min(_budgetKbps, AdaptiveQualityController.ToQuality(quality.Preset).BitrateKbps));
        };
    }
}
