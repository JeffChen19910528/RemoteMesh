using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Clipboard;

public sealed record ClipboardReadResult(ClipboardResult Result, byte[]? Data)
{
    public bool Ok => Result == ClipboardResult.Ok;
}

/// <summary>Viewer side. One request in flight at a time; every call is an explicit user action.</summary>
public sealed class ClipboardClient
{
    private readonly IChannel _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<ClipboardMessage>? _pending;

    public ClipboardClient(IChannel channel)
    {
        if (channel.Type != ChannelType.Clipboard)
            throw new ArgumentException("Clipboard traffic must use the Clipboard channel.", nameof(channel));
        _channel = channel;
    }

    /// <summary>Pumps host replies; run for the lifetime of the session.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var raw in _channel.ReceiveAsync(ct))
                if (ClipboardMessage.TryParse(raw.Span, out var msg) && msg is not null)
                    _pending?.TrySetResult(msg);
        }
        catch (OperationCanceledException) { }
        finally { _pending?.TrySetCanceled(); }
    }

    public async Task<ClipboardReadResult> ReadAsync(ClipboardFormat format, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var reply = await RoundTripAsync(new ClipboardMessage(ClipboardMessageType.ReadRequest, format, []),
            timeout, ct);
        return reply.Type switch
        {
            ClipboardMessageType.Data => new ClipboardReadResult(ClipboardResult.Ok, reply.Payload),
            ClipboardMessageType.Denied => new ClipboardReadResult(ReasonOf(reply), null),
            _ => new ClipboardReadResult(ClipboardResult.Malformed, null)
        };
    }

    public async Task<ClipboardResult> WriteAsync(ClipboardFormat format, byte[] data, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (data.Length == 0) return ClipboardResult.Empty;
        if (data.Length > ClipboardLimits.MaxFor(format)) return ClipboardResult.TooLarge;

        var reply = await RoundTripAsync(new ClipboardMessage(ClipboardMessageType.WriteRequest, format, data),
            timeout, ct);
        return reply.Type switch
        {
            ClipboardMessageType.Ack => ClipboardResult.Ok,
            ClipboardMessageType.Denied => ReasonOf(reply),
            _ => ClipboardResult.Malformed
        };
    }

    public Task<ClipboardResult> WriteTextAsync(string text, TimeSpan? timeout = null, CancellationToken ct = default) =>
        WriteAsync(ClipboardFormat.Text, ClipboardMessage.EncodeText(text), timeout, ct);

    private static ClipboardResult ReasonOf(ClipboardMessage denied) =>
        denied.Payload.Length == 1 && Enum.IsDefined((ClipboardResult)denied.Payload[0])
            ? (ClipboardResult)denied.Payload[0]
            : ClipboardResult.Malformed;

    private async Task<ClipboardMessage> RoundTripAsync(ClipboardMessage request, TimeSpan? timeout,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var tcs = new TaskCompletionSource<ClipboardMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            await _channel.SendAsync(request.ToBytes(), ct);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }
}
