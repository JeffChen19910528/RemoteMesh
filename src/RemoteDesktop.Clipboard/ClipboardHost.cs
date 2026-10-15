using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Clipboard;

public sealed class ClipboardHostOptions
{
    public int MaxRequestsPerWindow { get; init; } = 20;
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Host side. Serves clipboard requests only when ALL hold: session Connected, the host's local
/// <see cref="ClipboardPolicy"/> allows it, and the session was granted ClipboardRead/ClipboardWrite.
/// Nothing is pushed automatically (no background sync), and clipboard content is never logged.
/// </summary>
public sealed class ClipboardHost
{
    private readonly IChannel _channel;
    private readonly SessionInfo _session;
    private readonly IClipboardManager _clipboard;
    private readonly ClipboardHostOptions _opt;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<ClipboardMessageType, ClipboardResult>? _onDecision;

    private DateTimeOffset _windowStart;
    private int _count;

    public ClipboardHost(IChannel channel, SessionInfo session, IClipboardManager clipboard,
        ClipboardHostOptions? options = null, Func<DateTimeOffset>? clock = null,
        Action<ClipboardMessageType, ClipboardResult>? onDecision = null)
    {
        if (channel.Type != ChannelType.Clipboard)
            throw new ArgumentException("Clipboard traffic must use the Clipboard channel.", nameof(channel));
        _channel = channel;
        _session = session;
        _clipboard = clipboard;
        _opt = options ?? new ClipboardHostOptions();
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _onDecision = onDecision;
        _windowStart = _now();
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var raw in _channel.ReceiveAsync(ct))
                await HandleAsync(raw, ct);
        }
        catch (OperationCanceledException) { }
    }

    internal async Task HandleAsync(ReadOnlyMemory<byte> raw, CancellationToken ct)
    {
        if (!ClipboardMessage.TryParse(raw.Span, out var msg) || msg is null)
        {
            await ReplyDenied(ClipboardMessageType.Denied, ClipboardFormat.Text, ClipboardResult.Malformed, ct);
            return;
        }

        switch (msg.Type)
        {
            case ClipboardMessageType.ReadRequest:
                await HandleReadAsync(msg, ct);
                break;
            case ClipboardMessageType.WriteRequest:
                await HandleWriteAsync(msg, ct);
                break;
            default: // Data/Ack/Denied are host→viewer only
                await ReplyDenied(msg.Type, msg.Format, ClipboardResult.Malformed, ct);
                break;
        }
    }

    private async Task HandleReadAsync(ClipboardMessage msg, CancellationToken ct)
    {
        var gate = Check(Permission.ClipboardRead, canRead: true);
        if (gate != ClipboardResult.Ok) { await ReplyDenied(msg.Type, msg.Format, gate, ct); return; }

        byte[]? payload = msg.Format == ClipboardFormat.Text
            ? await GetTextBytesAsync(ct)
            : await _clipboard.GetImageAsync(ct);

        if (payload is null || payload.Length == 0)
        {
            await ReplyDenied(msg.Type, msg.Format, ClipboardResult.Empty, ct);
            return;
        }
        if (payload.Length > ClipboardLimits.MaxFor(msg.Format))
        {
            await ReplyDenied(msg.Type, msg.Format, ClipboardResult.TooLarge, ct);
            return;
        }

        _onDecision?.Invoke(msg.Type, ClipboardResult.Ok);
        await _channel.SendAsync(new ClipboardMessage(ClipboardMessageType.Data, msg.Format, payload).ToBytes(), ct);
    }

    private async Task HandleWriteAsync(ClipboardMessage msg, CancellationToken ct)
    {
        var gate = Check(Permission.ClipboardWrite, canRead: false);
        if (gate != ClipboardResult.Ok) { await ReplyDenied(msg.Type, msg.Format, gate, ct); return; }

        if (msg.Format == ClipboardFormat.Text)
        {
            if (!ClipboardMessage.TryDecodeText(msg.Payload, out var text))
            {
                await ReplyDenied(msg.Type, msg.Format, ClipboardResult.Malformed, ct);
                return;
            }
            await _clipboard.SetTextAsync(text, ct);
        }
        else
        {
            try { await _clipboard.SetImageAsync(msg.Payload, ct); }
            catch (NotSupportedException)
            {
                // This host cannot take images: refuse instead of letting the exception end the channel.
                await ReplyDenied(msg.Type, msg.Format, ClipboardResult.PolicyDenied, ct);
                return;
            }
        }

        _onDecision?.Invoke(msg.Type, ClipboardResult.Ok);
        await _channel.SendAsync(new ClipboardMessage(ClipboardMessageType.Ack, msg.Format, []).ToBytes(), ct);
    }

    private async Task<byte[]?> GetTextBytesAsync(CancellationToken ct)
    {
        var text = await _clipboard.GetTextAsync(ct);
        return string.IsNullOrEmpty(text) ? null : ClipboardMessage.EncodeText(text);
    }

    private ClipboardResult Check(Permission required, bool canRead)
    {
        if (_session.State != SessionState.Connected || _session.IsExpired)
            return ClipboardResult.SessionNotActive;

        var policy = _clipboard.Policy;
        var policyAllows = canRead
            ? policy is ClipboardPolicy.ReadOnly or ClipboardPolicy.Bidirectional
            : policy is ClipboardPolicy.WriteOnly or ClipboardPolicy.Bidirectional;
        if (!policyAllows) return ClipboardResult.PolicyDenied;

        if (!_session.Permissions.Has(required)) return ClipboardResult.PermissionDenied;

        var now = _now();
        if (now - _windowStart >= _opt.Window)
        {
            _windowStart = now;
            _count = 0;
        }
        return ++_count > _opt.MaxRequestsPerWindow ? ClipboardResult.RateLimited : ClipboardResult.Ok;
    }

    private async Task ReplyDenied(ClipboardMessageType requestType, ClipboardFormat format,
        ClipboardResult reason, CancellationToken ct)
    {
        _onDecision?.Invoke(requestType, reason);
        await _channel.SendAsync(ClipboardMessage.Denied(format, reason).ToBytes(), ct);
    }
}
