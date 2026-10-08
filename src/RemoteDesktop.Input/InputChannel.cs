using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Input;

/// <summary>Viewer side: serializes events onto the Input channel.</summary>
public sealed class InputSender
{
    private readonly IChannel _channel;

    public InputSender(IChannel channel)
    {
        if (channel.Type != ChannelType.Input)
            throw new ArgumentException("Input must be sent on the Input channel.", nameof(channel));
        _channel = channel;
    }

    public Task SendAsync(InputEvent e, CancellationToken ct = default) =>
        _channel.SendAsync(InputEventSerializer.Serialize(e), ct);
}

/// <summary>Host side: Input channel → <see cref="InputGate"/>. Rejected events are dropped, never fatal.</summary>
public sealed class InputReceiver
{
    private readonly IChannel _channel;
    private readonly InputGate _gate;

    public InputReceiver(IChannel channel, InputGate gate)
    {
        if (channel.Type != ChannelType.Input)
            throw new ArgumentException("Input must be received on the Input channel.", nameof(channel));
        _channel = channel;
        _gate = gate;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var raw in _channel.ReceiveAsync(ct))
                await _gate.ProcessRawAsync(raw, ct);
        }
        catch (OperationCanceledException) { }
    }
}
